using System.ComponentModel.DataAnnotations;
using Gizmo.Extensibility.Abstractions;
using Gizmo.Server.Bots.Telegram.Localization;

namespace Gizmo.Server.Bots.Telegram
{
    /// <summary>
    /// Bot options.
    /// </summary>
    public sealed class BotOptions
    {
        /// <summary>
        /// API key for the Telegram bot.
        /// </summary>
        [Name("Bot API key", nameof(Resources.TELEGRAM_BOT_OPTION_NAME_API_KEY))]
        [ExtendedDescription("Specify the API key for the Telegram bot.", nameof(Resources.TELEGRAM_BOT_OPTION_DESCRIPTION_API_KEY))]
        [Required()]
        [ModuleConfigSensitive()]
        public required string ApiKey { get; set; }
    }
}
