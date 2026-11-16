using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using Booktibus.Helpers;
using Booktibus.Models;

namespace Booktibus.Services
{
    /// <summary>
    /// PayOS REST API integration for fine payments in Booktibus.
    /// Uses native HttpClient with HMAC-SHA256 signature verification.
    /// Connects directly to PayOS v2 endpoints and manages real-time status polling.
    /// </summary>
    public class PayOSPaymentService
    {
        private const string ClientId = "7177dfdf-b20d-4129-b3a8-d0ab0547b017";
        private const string ApiKey = "eae8f33b-e649-47e6-9084-30571eecdbcf";
        private const string ChecksumKey = "bee1d07655f4283008d82204f70750f24da229cbf537258cfd7ccfc1cb70adca";
        private const string BaseUrl = "https://api-merchant.payos.vn";

        private static readonly HttpClient HttpClient = new();

        private readonly FineManagementService _fineManagementService;
        private DispatcherTimer? _pollTimer;

        public event EventHandler<FineItemModel>? PaymentSucceeded;
        public event EventHandler<string>? MembershipPaymentSucceeded;
        public event EventHandler<string>? PaymentStatusUpdated;

        public PayOSPaymentService()
        {
            _fineManagementService = null!;
        }

        public PayOSPaymentService(FineManagementService fineManagementService)
        {
            _fineManagementService = fineManagementService;
        }

        public async Task<PayOSPaymentLinkResult?> CreateFinePaymentLinkAsync(FineItemModel fine)
        {
            if (fine == null || fine.FineId <= 0 || fine.Amount <= 0)
            {
                return null;
            }

            try
            {
                StopPolling();

                long orderCode = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                int amount = (int)Math.Round(fine.Amount);
                string description = $"BTB FINE {fine.FineId}";
                if (description.Length > 25)
                {
                    description = description[..25];
                }

                string cancelUrl = "https://booktibus.local/cancel";
                string returnUrl = "https://booktibus.local/success";

                // Compute HMAC-SHA256 signature required by PayOS
                // Signature data format: amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}
                string dataToSign = $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";
                string signature = CreateHmacSha256(dataToSign, ChecksumKey);

                var payload = new
                {
                    orderCode = orderCode,
                    amount = amount,
                    description = description,
                    cancelUrl = cancelUrl,
                    returnUrl = returnUrl,
                    signature = signature,
                    items = new[]
                    {
                        new
                        {
                            name = $"Phạt {fine.FineCode}",
                            quantity = 1,
                            price = amount
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/payment-requests");
                request.Headers.Add("x-client-id", ClientId);
                request.Headers.Add("x-api-key", ApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await HttpClient.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                using JsonDocument doc = JsonDocument.Parse(responseJson);
                JsonElement root = doc.RootElement;

                string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                if (code == "00" && root.TryGetProperty("data", out var dataElem))
                {
                    string checkoutUrl = dataElem.TryGetProperty("checkoutUrl", out var cu) ? cu.GetString() ?? "" : "";
                    string qrCode = dataElem.TryGetProperty("qrCode", out var qr) ? qr.GetString() ?? "" : "";

                    // Start background polling to detect when user scans & pays
                    StartPolling(orderCode, fine);

                    return new PayOSPaymentLinkResult
                    {
                        OrderCode = orderCode,
                        CheckoutUrl = checkoutUrl,
                        QrCode = qrCode
                    };
                }

                string desc = root.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "Không thể tạo liên kết thanh toán";
                PaymentStatusUpdated?.Invoke(this, $"PayOS: {desc}");
                return null;
            }
            catch (Exception ex)
            {
                AppDiagnosticLogger.LogException(nameof(PayOSPaymentService), nameof(CreateFinePaymentLinkAsync), ex);
                PaymentStatusUpdated?.Invoke(this, "Lỗi kết nối PayOS: " + ex.Message);
                return null;
            }
        }

        public async Task<PayOSPaymentLinkResult?> CreateMembershipPaymentLinkAsync(string tier, int amount, int userId)
        {
            if (string.IsNullOrWhiteSpace(tier) || amount <= 0)
            {
                return null;
            }

            try
            {
                StopPolling();

                long orderCode = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string description = $"BTB {tier} {userId}";
                if (description.Length > 25)
                {
                    description = description[..25];
                }

                string cancelUrl = "https://booktibus.local/cancel";
                string returnUrl = "https://booktibus.local/success";

                string dataToSign = $"amount={amount}&cancelUrl={cancelUrl}&description={description}&orderCode={orderCode}&returnUrl={returnUrl}";
                string signature = CreateHmacSha256(dataToSign, ChecksumKey);

                var payload = new
                {
                    orderCode = orderCode,
                    amount = amount,
                    description = description,
                    cancelUrl = cancelUrl,
                    returnUrl = returnUrl,
                    signature = signature,
                    items = new[]
                    {
                        new
                        {
                            name = $"Gói {tier}",
                            quantity = 1,
                            price = amount
                        }
                    }
                };

                using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/v2/payment-requests");
                request.Headers.Add("x-client-id", ClientId);
                request.Headers.Add("x-api-key", ApiKey);
                request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await HttpClient.SendAsync(request);
                string responseJson = await response.Content.ReadAsStringAsync();

                using JsonDocument doc = JsonDocument.Parse(responseJson);
                JsonElement root = doc.RootElement;

                string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
                if (code == "00" && root.TryGetProperty("data", out var dataElem))
                {
                    string checkoutUrl = dataElem.TryGetProperty("checkoutUrl", out var cu) ? cu.GetString() ?? "" : "";
                    string qrCode = dataElem.TryGetProperty("qrCode", out var qr) ? qr.GetString() ?? "" : "";

                    StartMembershipPolling(orderCode, tier);

                    return new PayOSPaymentLinkResult
                    {
                        OrderCode = orderCode,
                        CheckoutUrl = checkoutUrl,
                        QrCode = qrCode
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                AppDiagnosticLogger.LogException(nameof(PayOSPaymentService), nameof(CreateMembershipPaymentLinkAsync), ex);
                return null;
            }
        }

        private void StartMembershipPolling(long orderCode, string tier)
        {
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2)
            };

            _pollTimer.Tick += async (_, _) =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v2/payment-requests/{orderCode}");
                    request.Headers.Add("x-client-id", ClientId);
                    request.Headers.Add("x-api-key", ApiKey);

                    using var response = await HttpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        if (root.TryGetProperty("code", out var c) && c.GetString() == "00" &&
                            root.TryGetProperty("data", out var dataElem))
                        {
                            string status = dataElem.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                            if (string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase))
                            {
                                StopPolling();
                                PaymentStatusUpdated?.Invoke(this, $"Thanh toán thành công gói {tier} qua PayOS!");
                                MembershipPaymentSucceeded?.Invoke(this, tier);
                            }
                            else if (string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                            {
                                StopPolling();
                            }
                        }
                    }
                }
                catch
                {
                }
            };

            _pollTimer.Start();
        }

        private void StartPolling(long orderCode, FineItemModel fine)
        {
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };

            _pollTimer.Tick += async (_, _) =>
            {
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/v2/payment-requests/{orderCode}");
                    request.Headers.Add("x-client-id", ClientId);
                    request.Headers.Add("x-api-key", ApiKey);

                    using var response = await HttpClient.SendAsync(request);
                    if (response.IsSuccessStatusCode)
                    {
                        string json = await response.Content.ReadAsStringAsync();
                        using JsonDocument doc = JsonDocument.Parse(json);
                        JsonElement root = doc.RootElement;

                        if (root.TryGetProperty("code", out var c) && c.GetString() == "00" &&
                            root.TryGetProperty("data", out var dataElem))
                        {
                            string status = dataElem.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
                            if (string.Equals(status, "PAID", StringComparison.OrdinalIgnoreCase))
                            {
                                StopPolling();

                                // Automatically update database
                                if (fine.FineId.HasValue)
                                {
                                    _fineManagementService.RecordOnlineFinePayment(fine.FineId.Value, "PayOS VietQR", orderCode.ToString());
                                }

                                PaymentStatusUpdated?.Invoke(this, "Thanh toán thành công qua PayOS!");
                                PaymentSucceeded?.Invoke(this, fine);
                            }
                            else if (string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase))
                            {
                                StopPolling();
                                PaymentStatusUpdated?.Invoke(this, "Đơn thanh toán đã bị hủy.");
                            }
                        }
                    }
                }
                catch
                {
                    // Ignore transient network errors during poll
                }
            };

            _pollTimer.Start();
        }

        public void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer = null;
            }
        }

        private static string CreateHmacSha256(string data, string key)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            byte[] dataBytes = Encoding.UTF8.GetBytes(data);
            using var hmac = new HMACSHA256(keyBytes);
            byte[] hashBytes = hmac.ComputeHash(dataBytes);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }
    }

    public class PayOSPaymentLinkResult
    {
        public long OrderCode { get; init; }
        public string CheckoutUrl { get; init; } = string.Empty;
        public string QrCode { get; init; } = string.Empty;
    }
}
