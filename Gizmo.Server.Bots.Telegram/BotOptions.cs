using System.ComponentModel;
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

        /// <summary>
        /// Confirmation code message sent to the user. Use the <c>{code}</c> token to include the code.
        /// </summary>
        [Name("Confirmation code message", nameof(Resources.TELEGRAM_BOT_OPTION_NAME_CONFIRMATION_CODE_MESSAGE))]
        [ExtendedDescription("Message sent with the verification code. Use the {code} token to include the code.", nameof(Resources.TELEGRAM_BOT_OPTION_DESCRIPTION_CONFIRMATION_CODE_MESSAGE))]
        [DefaultValue("Your confirmation code: {code}")]
        public string? ConfirmationCodeMessage { get; set; }

        /// <summary>
        /// Prompt asking the user to share their phone number.
        /// </summary>
        [Name("Share phone prompt", nameof(Resources.TELEGRAM_BOT_OPTION_NAME_SHARE_PHONE_MESSAGE))]
        [ExtendedDescription("Message asking the user to share their phone number to complete verification.", nameof(Resources.TELEGRAM_BOT_OPTION_DESCRIPTION_SHARE_PHONE_MESSAGE))]
        [DefaultValue("Please share your phone number to complete verification.")]
        public string? SharePhonePromptMessage { get; set; }

        /// <summary>
        /// Message sent when verification completes.
        /// </summary>
        [Name("Verification complete message", nameof(Resources.TELEGRAM_BOT_OPTION_NAME_VERIFICATION_COMPLETE_MESSAGE))]
        [ExtendedDescription("Message sent to the user once verification is complete.", nameof(Resources.TELEGRAM_BOT_OPTION_DESCRIPTION_VERIFICATION_COMPLETE_MESSAGE))]
        [DefaultValue("Thank you! Verification complete.")]
        public string? VerificationCompleteMessage { get; set; }

        /// <summary>
        /// Label for the button that requests the user's contact.
        /// </summary>
        [Name("Contact button label", nameof(Resources.TELEGRAM_BOT_OPTION_NAME_SHARE_CONTACT_BUTTON_LABEL))]
        [ExtendedDescription("Label for the button used to request the user's phone number.", nameof(Resources.TELEGRAM_BOT_OPTION_DESCRIPTION_SHARE_CONTACT_BUTTON_LABEL))]
        [DefaultValue("Share phone number")]
        public string? ShareContactButtonLabel { get; set; }
    }
}
