using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Gizmo.Extensibility.Abstractions;
using Gizmo.Server.Bots.Telegram.Localization;
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
    [Name("Telegram bot", nameof(Resources.TELEGRAM_BOT_MODULE_NAME))]
    [ExtendedDescription("Telegram messenger integration for user verification and notifications.", nameof(Resources.TELEGRAM_BOT_MODULE_DESCRIPTION))]
    [MessengerChannel(CommunicationChannels.Telegram)]
    public sealed class Bot : IModuleStart, IModuleStop, IModuleInitialize,
        IVerificationRedirectHandler, IVerificationCodeDispatchHandler, ICanProvidePhone
    {
        #region CONSTRUCTOR

        public Bot(Gizmo.DAL.Contexts.IGizmoDbContextProviderConcrete contextProvider,
            ILogger<Bot> logger,
            ModuleContext moduleContext,
            IOptionsMonitor<BotOptions> options,
            IMessageSubscriber subscriber,
            IVerificationCallback verificationCallback)
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
        private readonly IVerificationCallback _verificationCallback;
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
        /// Pending link nonces. Maps nonce → token value.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _pendingLinks = new();

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

        #region IVerificationRedirectHandler

        public Task<VerificationRedirectResult> CreateRedirectUrlAsync(CreateRedirectUrlContext context, CancellationToken cancellationToken = default)
        {
            var nonce = Guid.NewGuid().ToString("N");

            _pendingLinks[nonce] = context.TokenValue;

            var botUsername = _botUsername
                ?? throw new InvalidOperationException("Telegram bot is not connected. Cannot create redirect URL.");

            return Task.FromResult(new VerificationRedirectResult
            {
                RedirectUrl = $"https://t.me/{botUsername}?start={nonce}",
                ExpiresInSeconds = 300,
            });
        }

        #endregion

        #region IVerificationCodeDispatchHandler

        public async Task<SendCodeResult> SendCodeAsync(SendCodeContext context, CancellationToken cancellationToken = default)
        {
            if (_botClient is null)
            {
                _logger.LogWarning("Cannot send confirmation code: Telegram bot client is not initialized.");
                return SendCodeResult.Error;
            }

            if (!long.TryParse(context.ChannelValue, out var chatId))
            {
                _logger.LogWarning("Invalid channel value (not a valid chat ID): {Value}.", context.ChannelValue);
                return SendCodeResult.Error;
            }

            try
            {
                await _botClient.SendMessage(chatId, $"Your confirmation code: {context.Code}", cancellationToken: cancellationToken);
                return SendCodeResult.Sent;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send confirmation code to chat {ChatId}.", chatId);
                return SendCodeResult.Error;
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
        /// Always asks for phone number sharing via contact button.
        /// </summary>
        private async Task HandleStartCommandAsync(string nonce, long chatId, User? user,
            CancellationToken cancellationToken = default)
        {
            if (!_pendingLinks.TryRemove(nonce, out var tokenValue))
            {
                _logger.LogWarning("Unknown or expired nonce received: {Nonce}.", nonce);
                return;
            }

            // store pending contact — wait for user to share phone number
            _pendingContacts[chatId] = new PendingContact(tokenValue, user);

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

            await _verificationCallback.OnCallbackAsync(new VerificationCallbackResult
            {
                TokenValue = pending.TokenValue,
                RecipientAddress = chatId.ToString(),
                Metadata = metadata,
            }, cancellationToken);
        }

        private static Dictionary<VerificationCallbackMetadataKey, string> BuildMetadata(User? user, string? phoneNumber)
        {
            var metadata = new Dictionary<VerificationCallbackMetadataKey, string>
            {
                [VerificationCallbackMetadataKey.ChannelType] = CommunicationChannels.Telegram,
            };

            if (!string.IsNullOrEmpty(user?.FirstName))
                metadata[VerificationCallbackMetadataKey.FirstName] = user.FirstName;
            if (!string.IsNullOrEmpty(user?.LastName))
                metadata[VerificationCallbackMetadataKey.LastName] = user.LastName;
            if (!string.IsNullOrEmpty(phoneNumber))
                metadata[VerificationCallbackMetadataKey.PhoneNumber] = phoneNumber;

            return metadata;
        }

        private Task ProcessMessageAsync(IAPIEventMessage eventMessage, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Processing message: {Message}.", eventMessage.GetType());
            return Task.CompletedTask;
        }

        private sealed record PendingContact(string TokenValue, User? User);

        #endregion
    }
}
