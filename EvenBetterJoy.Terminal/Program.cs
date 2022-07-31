﻿using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EvenBetterJoy.Domain.Services;
using EvenBetterJoy.Domain.Models;
using EvenBetterJoy.Domain.Hid;

namespace EvenBetterJoy.Terminal
{
    public class Program
    {
        static async Task Main(string[] args)
        {
            //TODO: arg parser and maybe a cool ascii logo
            
            await Host.CreateDefaultBuilder(args)
                .UseContentRoot(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location))
                .ConfigureLogging(logging =>
                {
                    logging.AddConsole();
                })
                .ConfigureServices((context, services) =>
                {
                    //TODO: get off singletons after setting up DI
                    services
                        .AddHostedService<ApplicationHostedService>()
                        .AddTransient<IEvenBetterJoyApplication, EvenBetterJoyApplication>()
                        .AddTransient<IHidService, HidService>()
                        .AddSingleton<IJoyconManager, JoyconManager>()
                        .AddSingleton<IVirtualGamepadService, VirtualGamepadService>()
                        .AddSingleton<IHidGuardianService, HidGuardianService>()
                        .AddSingleton<ICommunicationService, CommunicationService>()
                        .AddSingleton<ISettingsService, SettingsService>();

                    services
                        .AddOptions<Settings>()
                        .Bind(context.Configuration.GetSection("Settings"))
                        .ValidateOnStart();
                })
                .RunConsoleAsync();
        }
    }
}
