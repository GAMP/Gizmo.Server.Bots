using System.ComponentModel.DataAnnotations;
using Gizmo.Extensibility.Abstractions;

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
        [Name("Bot API key")]
        [ExtendedDescription("Specify the API key for the Telegram bot.")]
        [Required()]
        [ModuleConfigSensitive()]
        public required string ApiKey { get; set; }
    }
}
