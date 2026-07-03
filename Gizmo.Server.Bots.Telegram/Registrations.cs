using System.ComponentModel;
using System.Reflection;
using Gizmo.Extensibility.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Gizmo.Server.Bots.Telegram
{
    public sealed class Registrations : IServiceRegistrar
    {
        public void RegisterServices(IServiceCollection services)
        {
            services.PostConfigure<BotOptions>(ApplyStringDefaults);
        }

        private static void ApplyStringDefaults(BotOptions options)
        {
            foreach (var prop in typeof(BotOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType != typeof(string) || !prop.CanWrite)
                    continue;

                if (prop.GetCustomAttribute<DefaultValueAttribute>()?.Value is not string defaultValue)
                    continue;

                if (string.IsNullOrWhiteSpace((string?)prop.GetValue(options)))
                    prop.SetValue(options, defaultValue);
            }
        }
    }
}
