using System.Collections.Concurrent;
using Gizmo.Extensibility.Abstractions;
using Gizmo.Server.Extensibility;
using Gizmo.Web.Api.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reactive.Linq;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Gizmo.Server.Bots.Telegram
{
    [ModuleOptions(typeof(BotOptions))]
    [ModuleMetadata("Telegram bot", "4759f871-42c2-490d-8d75-fc45a60a812c")]
    [MessengerChannel(CommunicationChannels.Telegram)]
    public sealed class Bot : IModuleStart, IModuleStop, IModuleInitialize, IMessengerVerificationHandler
    {
        #region CONSTRUCTOR

        public Bot(Gizmo.DAL.Contexts.IGizmoDbContextProviderConcrete contextProvider,
            ILogger<Bot> logger,
            ModuleContext moduleContext,
            IOptionsMonitor<BotOptions> options,
            IMessageSubscriber subscriber,
            IMessengerVerificationCallback verificationCallback)
        {
            _contextProvider = contextProvider;
            _logger = logger;
            _moduleContext = moduleContext;
            _options = options;
            _subscriber = subscriber;
            _verificationCallback = verificationCallback;

            options.OnChange((newOptions, name) =>
            {
                _logger.LogInformation("Bot options changed, restarting Telegram client.");
                _ = RestartPollingAsync();
            });
        }

        #endregion

        #region FIELDS

        private readonly Gizmo.DAL.Contexts.IGizmoDbContextProviderConcrete _contextProvider;
        private readonly ILogger<Bot> _logger;
        private readonly ModuleContext _moduleContext;
        private readonly IOptionsMonitor<BotOptions> _options;
        private readonly IMessageSubscriber _subscriber;
        private readonly IMessengerVerificationCallback _verificationCallback;
        private IDisposable? _subscription;

        private TelegramBotClient? _botClient;
        private CancellationTokenSource? _pollCts;
        private string? _botUsername;
        private readonly SemaphoreSlim _restartLock = new(1, 1);

        private readonly MessageSubscriptionOptions _subscriptionOptions = new()
        {
            BufferSize = 1000,
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
        };

        /// <summary>
        /// Pending link nonces. Maps nonce → pending link state.
        /// </summary>
        private readonly ConcurrentDictionary<string, PendingLink> _pendingLinks = new();

        /// <summary>
        /// Pending contact shares. Maps chatId → pending verification state (waiting for user to share phone number).
        /// </summary>
        private readonly ConcurrentDictionary<long, PendingContact> _pendingContacts = new();

        #endregion

        #region IModuleInitialize

        public Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            _subscription = _subscriber.Observe<IAPIEventMessage>(_subscriptionOptions)
                .Where(m => !(m is EntityChangeEventMessage ev && ev.EntityType == "Setting"))
                .Select(m => Observable.FromAsync((ct) => ProcessMessageAsync(m, ct)))
                .Merge(maxConcurrent: 4)
                .Subscribe(_ => { }, ex => _logger.LogError(ex, "Message processing error."));

            return Task.CompletedTask;
        }

        #endregion

        #region IModuleStart

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            await StartPollingCoreAsync(cancellationToken);
        }

        #endregion

        #region IModuleStop

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _subscription?.Dispose();
            StopPollingCore();
            return Task.CompletedTask;
        }

        #endregion

        #region IMessengerVerificationHandler

        public Task<MessagingLinkResult> CreateVerificationLinkAsync(CreateVerificationLinkContext context, CancellationToken cancellationToken = default)
        {
            var nonce = Guid.NewGuid().ToString("N");

            _pendingLinks[nonce] = new PendingLink(context.TokenValue, ConfirmationCode: null);

            var botUsername = _botUsername
                ?? throw new InvalidOperationException("Telegram bot is not connected. Cannot create verification link.");

            return Task.FromResult(new MessagingLinkResult
            {
                LinkUrl = $"https://t.me/{botUsername}?start={nonce}",
                Nonce = nonce,
                ExpiresInSeconds = 300,
            });
        }

        public Task<MessagingLinkResult> CreateRegistrationLinkAsync(CreateRegistrationLinkContext context, CancellationToken cancellationToken = default)
        {
            var nonce = Guid.NewGuid().ToString("N");

            _pendingLinks[nonce] = new PendingLink(context.TokenValue, context.ConfirmationCode);

            var botUsername = _botUsername
                ?? throw new InvalidOperationException("Telegram bot is not connected. Cannot create registration link.");

            return Task.FromResult(new MessagingLinkResult
            {
                LinkUrl = $"https://t.me/{botUsername}?start={nonce}",
                Nonce = nonce,
                ExpiresInSeconds = 300,
            });
        }

        public async Task<bool> SendConfirmationCodeAsync(SendConfirmationCodeContext context, CancellationToken cancellationToken = default)
        {
            if (_botClient is null)
            {
                _logger.LogWarning("Cannot send confirmation code: Telegram bot client is not initialized.");
                return false;
            }

            if (!long.TryParse(context.RecipientAddress, out var chatId))
            {
                _logger.LogWarning("Invalid recipient address (not a valid chat ID): {Address}.", context.RecipientAddress);
                return false;
            }

            try
            {
                await _botClient.SendMessage(chatId, $"Your confirmation code: {context.ConfirmationCode}", cancellationToken: cancellationToken);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation code to chat {ChatId}.", chatId);
                return false;
            }
        }

        #endregion

        #region PRIVATE

        private async Task StartPollingCoreAsync(CancellationToken cancellationToken)
        {
            var apiKey = _options.CurrentValue.ApiKey;

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogWarning("Telegram bot API key is not configured. Bot will not start.");
                return;
            }

            try
            {
                var client = new TelegramBotClient(apiKey);
                var me = await client.GetMe(cancellationToken);

                _botClient = client;
                _botUsername = me.Username;
                _pollCts = new CancellationTokenSource();

                _logger.LogInformation("Telegram bot started as @{Username}.", _botUsername);

                _ = PollUpdatesAsync(_pollCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start Telegram bot. Check the API key.");
                _botClient = null;
                _botUsername = null;
            }
        }

        private void StopPollingCore()
        {
            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            _botClient = null;
            _botUsername = null;
        }

        private async Task RestartPollingAsync()
        {
            await _restartLock.WaitAsync();
            try
            {
                StopPollingCore();
                await StartPollingCoreAsync(CancellationToken.None);
            }
            finally
            {
                _restartLock.Release();
            }
        }

        private async Task PollUpdatesAsync(CancellationToken cancellationToken)
        {
            var client = _botClient;
            if (client is null)
                return;

            int offset = 0;

            // drop pending updates on startup
            try
            {
                var pending = await client.GetUpdates(offset: -1, limit: 1, timeout: 0, cancellationToken: cancellationToken);
                if (pending.Length > 0)
                    offset = pending[^1].Id + 1;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Update[] updates;
                    try
                    {
                        updates = await client.GetUpdates(
                            offset: offset,
                            limit: 100,
                            timeout: 30,
                            allowedUpdates: [UpdateType.Message],
                            cancellationToken: cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error fetching Telegram updates. Retrying in 5 seconds.");
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                        continue;
                    }

                    foreach (var update in updates)
                    {
                        offset = update.Id + 1;

                        try
                        {
                            await HandleUpdateAsync(update, cancellationToken);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "Error handling Telegram update {UpdateId}.", update.Id);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected during shutdown
            }

            _logger.LogInformation("Telegram polling stopped.");
        }

        private async Task HandleUpdateAsync(Update update, CancellationToken cancellationToken)
        {
            if (update.Message is not { } message)
                return;

            // handle contact share (second step of verification flow)
            if (message.Contact is { } contact)
            {
                await HandleContactAsync(message.Chat.Id, message.From, contact, cancellationToken);
                return;
            }

            // handle /start deep link
            if (message.Text is { } text && text.StartsWith("/start ", StringComparison.Ordinal))
            {
                var payload = text["/start ".Length..].Trim();
                if (!string.IsNullOrEmpty(payload))
                    await HandleStartCommandAsync(payload, message.Chat.Id, message.From, cancellationToken);
            }
        }

        /// <summary>
        /// Called when Telegram bot receives /start {nonce} from a user.
        /// </summary>
        private async Task HandleStartCommandAsync(string nonce, long chatId, User? user,
            CancellationToken cancellationToken = default)
        {
            if (!_pendingLinks.TryRemove(nonce, out var link))
            {
                _logger.LogWarning("Unknown or expired nonce received: {Nonce}.", nonce);
                return;
            }

            if (link.ConfirmationCode != null)
            {
                // registration flow — send code and complete immediately
                if (_botClient is not null)
                {
                    try
                    {
                        await _botClient.SendMessage(chatId, $"Your confirmation code: {link.ConfirmationCode}", cancellationToken: cancellationToken);
                        _logger.LogInformation("Confirmation code sent to chatId {ChatId}.", chatId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to send confirmation code to chatId {ChatId}.", chatId);
                    }
                }

                var metadata = BuildMetadata(user, phoneNumber: null);

                await _verificationCallback.OnCallbackAsync(new MessengerRegistrationCallbackResult
                {
                    TokenValue = link.TokenValue,
                    RecipientAddress = chatId.ToString(),
                    Metadata = metadata,
                }, cancellationToken);
            }
            else
            {
                // verification flow — ask user to share phone number
                _pendingContacts[chatId] = new PendingContact(link.TokenValue, user);

                if (_botClient is not null)
                {
                    var keyboard = new ReplyKeyboardMarkup(
                        new KeyboardButton[] { KeyboardButton.WithRequestContact("Share phone number") })
                    {
                        OneTimeKeyboard = true,
                        ResizeKeyboard = true,
                    };

                    await _botClient.SendMessage(chatId,
                        "Please share your phone number to complete verification.",
                        replyMarkup: keyboard,
                        cancellationToken: cancellationToken);
                }
            }
        }

        /// <summary>
        /// Called when user shares their contact after a verification /start.
        /// </summary>
        private async Task HandleContactAsync(long chatId, User? user, Contact contact, CancellationToken cancellationToken)
        {
            if (!_pendingContacts.TryRemove(chatId, out var pending))
            {
                _logger.LogDebug("Received contact from chatId {ChatId} with no pending verification.", chatId);
                return;
            }

            var metadata = BuildMetadata(user ?? pending.User, contact.PhoneNumber);

            // remove the keyboard
            if (_botClient is not null)
            {
                await _botClient.SendMessage(chatId,
                    "Thank you! Verification complete.",
                    replyMarkup: new ReplyKeyboardRemove(),
                    cancellationToken: cancellationToken);
            }

            await _verificationCallback.OnCallbackAsync(new MessengerVerificationCallbackResult
            {
                TokenValue = pending.TokenValue,
                RecipientAddress = chatId.ToString(),
                Metadata = metadata,
            }, cancellationToken);
        }

        private static Dictionary<MessengerCallbackMetadataKey, string> BuildMetadata(User? user, string? phoneNumber)
        {
            var metadata = new Dictionary<MessengerCallbackMetadataKey, string>
            {
                [MessengerCallbackMetadataKey.ChannelType] = CommunicationChannels.Telegram,
            };

            if (!string.IsNullOrEmpty(user?.FirstName))
                metadata[MessengerCallbackMetadataKey.FirstName] = user.FirstName;
            if (!string.IsNullOrEmpty(user?.LastName))
                metadata[MessengerCallbackMetadataKey.LastName] = user.LastName;
            if (!string.IsNullOrEmpty(phoneNumber))
                metadata[MessengerCallbackMetadataKey.PhoneNumber] = phoneNumber;

            return metadata;
        }

        private Task ProcessMessageAsync(IAPIEventMessage eventMessage, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Processing message: {Message}.", eventMessage.GetType());
            return Task.CompletedTask;
        }

        private sealed record PendingLink(string TokenValue, string? ConfirmationCode);
        private sealed record PendingContact(string TokenValue, User? User);

        #endregion
    }
}
