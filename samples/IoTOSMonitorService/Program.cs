using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace IoTOSMonitorService
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args).UseWindowsService()
                .ConfigureServices((hostContext, services) =>
                {
                    string mode = "";
                    string connectionString = "";
                    if (args.Count() == 2) {
                        if (args[0] != "dps" && args[0] != "manual") {
                            throw new ArgumentOutOfRangeException("args should be 'dps|manual connectionstring'");
                        }
                        mode = args[0];
                        connectionString = args[1];
                    } else {
                        IConfiguration config = hostContext.Configuration;
                        mode = config.GetConnectionString("Mode");
                        connectionString = config.GetConnectionString("IoTConnectionString");
                    }
                    var workerConfig = ParseArgsOrSettings(mode, connectionString);
                    services.AddSingleton(workerConfig);
                    services.AddHostedService<Worker>();
                });

        static string GetConfigValue(List<string>configs, string key)
        {
            string value = "";
            var specifiedItem = configs.Where(s => { return s.StartsWith(key); }).First();
            value = specifiedItem.Substring(specifiedItem.IndexOf("=")+1);
            return value;
        }

        static WorkerConfig ParseArgsOrSettings(string mode, string settings)
        {
            var workerConfig = new WorkerConfig()
            {
                Mode = mode,
                TestMessage = $"mode:{mode},settings:{settings}"
            };
            if (mode == "manual")
            {
                workerConfig.IoTHubConnectionString = settings;
            }
            else if (mode == "dps")
            {
                string exceptionMessage = "settings should be \"GlobalEndpoint=...;IDScope=...;DeviceId=...;SharedAccessKey...\"";
                if (!(settings.IndexOf("GlobalEndpoint") >= 0 && settings.IndexOf("DeviceId") >= 0 && settings.IndexOf("IDScope") >= 0 && settings.IndexOf("SharedAccessKey") >= 0))
                {
                    throw new ArgumentOutOfRangeException(exceptionMessage);
                }
                    var configSettings = new List<string>(settings.Split(new char[] { ';' }));
                if (configSettings.Count!=4)
                {
                    throw new ArgumentOutOfRangeException(exceptionMessage);
                }
                workerConfig.DPSGlobalEndpoint = GetConfigValue(configSettings, "GlobalEndpoint");
                workerConfig.DPSId = GetConfigValue(configSettings, "DeviceId");
                workerConfig.DPSIDScope = GetConfigValue(configSettings, "IDScope");
                workerConfig.DPSSASKey = GetConfigValue(configSettings, "SharedAccessKey");
            }
            else
            {
                throw new ArgumentOutOfRangeException("mode should be 'manual' or 'dps'.");
            }
            return workerConfig;
        }

    }
}
