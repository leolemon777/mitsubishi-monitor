using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MitsubishiMonitor.Demo.Services
{
    public class DingTalkService
    {
        private static DingTalkService _instance;
        public static DingTalkService Instance => _instance ??= new DingTalkService();

        private readonly HttpClient _httpClient;
        private string _webhookUrl = "";

        private DingTalkService()
        {
            _httpClient = new HttpClient();
        }

        public void SetWebhook(string webhookUrl)
        {
            _webhookUrl = webhookUrl;
        }

        public async Task SendAlertAsync(string title, string message)
        {
            if (string.IsNullOrEmpty(_webhookUrl))
            {
                System.Diagnostics.Debug.WriteLine("[钉钉] 未配置Webhook，跳过发送");
                return;
            }

            try
            {
                var payload = new
                {
                    msgtype = "markdown",
                    markdown = new
                    {
                        title = title,
                        text = $"### {title}\n\n{message}\n\n> 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                    }
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(_webhookUrl, content);
                var result = await response.Content.ReadAsStringAsync();

                System.Diagnostics.Debug.WriteLine($"[钉钉] 发送结果: {result}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[钉钉] 发送失败: {ex.Message}");
            }
        }

        public async Task SendDeviceOfflineAlertAsync(string deviceName, string ipAddress)
        {
            await SendAlertAsync(
                "⚠️ 设备掉线",
                $"**设备**: {deviceName}\n**IP**: {ipAddress}\n**状态**: 已离线"
            );
        }

        public async Task SendTemperatureAlarmAsync(string deviceName, float currentTemp, float targetTemp)
        {
            await SendAlertAsync(
                "🌡️ 温度报警",
                $"**设备**: {deviceName}\n**当前温度**: {currentTemp:F1}°C\n**目标温度**: {targetTemp:F1}°C\n**状态**: 温度超过目标值！"
            );
        }

        public async Task SendSsrFaultAlertAsync(string deviceName)
        {
            await SendAlertAsync(
                "🔌 SSR故障",
                $"**设备**: {deviceName}\n**故障**: 固态继电器粘连\n**说明**: PID停止输出但温度仍在上升，请检查SSR"
            );
        }
    }
}
