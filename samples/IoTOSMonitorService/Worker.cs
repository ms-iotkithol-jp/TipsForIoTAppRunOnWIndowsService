#define SEND_TELEMETRY
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Azure.Devices.Provisioning.Client.Transport;
using Microsoft.Azure.Devices.Provisioning.Client;

namespace IoTOSMonitorService
{
    public class WorkerConfig {
        public string IoTHubConnectionString { get; set; }
        public string TestMessage{ get; set; }
        public string Mode { get; set; }
        public string DPSId { get; set; }
        public string DPSGlobalEndpoint { get; set; }
        public string DPSIDScope { get; set; }
        public string DPSSASKey { get; set; }
    }

    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly WorkerConfig workerConfig;

        public Worker(ILogger<Worker> logger, WorkerConfig config)
        {
            _logger = logger;
            workerConfig = config;
        }

        private static readonly string PnPModelId = "dtmi:embeddedgeorge:example:HWMonitoringOnService;1";
        DeviceClient deviceClient = null;
        MonitorConfig monitorConfig = new MonitorConfig() { IntervalMSec = 60000 };
        MonitorReport monitorReport = new MonitorReport();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var option = new ClientOptions
            {
                ModelId = PnPModelId
            };
            if (workerConfig.Mode == "manual") {
                deviceClient = DeviceClient.CreateFromConnectionString(workerConfig.IoTHubConnectionString, options:option);
            }
            else if (workerConfig.Mode == "dps") {
                var security = new SecurityProviderSymmetricKey(workerConfig.DPSId, workerConfig.DPSSASKey, null);
                ProvisioningTransportHandler transportHandler = new ProvisioningTransportHandlerAmqp((TransportFallbackType)TransportType.Amqp);
                var provClient = ProvisioningDeviceClient.Create(
                    workerConfig.DPSGlobalEndpoint,
                    workerConfig.DPSIDScope,
                    security,
                    transportHandler
                );
                var registrationResult = await provClient.RegisterAsync();
                if (registrationResult.Status != ProvisioningRegistrationStatusType.Assigned)
                {
                    _logger.LogError($"Registration Failed - {registrationResult.Status}");
                }
                var auth = new DeviceAuthenticationWithRegistrySymmetricKey(
                    registrationResult.DeviceId,
                    security.GetPrimaryKey()
                );
                deviceClient = DeviceClient.Create(registrationResult.AssignedHub, auth, TransportType.Amqp, options:option);
            }
            else {
                _logger.LogError("bad mode!");
            }
            deviceClient.SetConnectionStatusChangesHandler((status, reason) => { _logger.LogInformation($"IoT Hub Connection status changed - status={status},reason={reason}"); });
            await deviceClient.SetDesiredPropertyUpdateCallbackAsync(desiredPropertyUpdateCallbackMethod, this);
            await CheckDeviceTwins();

            var cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");

            try {
                await deviceClient.OpenAsync();
                _logger.LogInformation("Connected to IoT Hub.");
                while (!stoppingToken.IsCancellationRequested)
                {
                    string timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    monitorReport.ProcessorTime = cpuCounter.NextValue();
                    monitorReport.Timestamp = timestamp;
                    await UpdateReportedProperties(monitorReport);
                    _logger.LogInformation($"Updated Reported Properties at: {timestamp}");

                    int intervalMSec = 0;
                    lock (monitorConfig) {
                        intervalMSec = monitorConfig.IntervalMSec;
                    }

#if SEND_TELEMETRY
                    var telemetry = new {
                        monitoring = monitorReport,
                        intervalMSec = intervalMSec
                    };
                    var telemetryJson = Newtonsoft.Json.JsonConvert.SerializeObject(telemetry);
                    var msg = new Message(System.Text.Encoding.UTF8.GetBytes(telemetryJson));
                    await deviceClient.SendEventAsync(msg);

#endif
                    await Task.Delay(monitorConfig.IntervalMSec, stoppingToken);
                }
            }
            catch (Exception ex) {
                _logger.LogError(ex.Message);
            }
        }

        private async Task CheckDeviceTwins()
        {
            var twins = await deviceClient.GetTwinAsync();
            UpdateMonitorConfig(twins.Properties.Desired.ToJson());
        }
        private async Task desiredPropertyUpdateCallbackMethod(TwinCollection desiredProperties, object userContext)
        {
            UpdateMonitorConfig(desiredProperties.ToJson());
        }

        private void UpdateMonitorConfig(string desiredPropertiesJson)
        {
            dynamic dpJson = Newtonsoft.Json.JsonConvert.DeserializeObject(desiredPropertiesJson);
            if (dpJson.configuration != null) {
                dynamic configJson = dpJson.configuration;
                if (configJson.IntervalMSec != null) {
                    lock (monitorConfig) {
                        monitorConfig.IntervalMSec = configJson.IntervalMSec;
                    }
                }
            }
        }


        private async Task UpdateReportedProperties(MonitorReport report)
        {
            var reported = new TwinCollection();
            reported["monitor"] = new TwinCollection(Newtonsoft.Json.JsonConvert.SerializeObject(report));
            await deviceClient.UpdateReportedPropertiesAsync(reported);
        }

    }

    class MonitorConfig {
        public int IntervalMSec { get; set; }
    }

    class MonitorReport {
        public float ProcessorTime{ get; set; }
        public string Timestamp { get; set; }
    }
}
