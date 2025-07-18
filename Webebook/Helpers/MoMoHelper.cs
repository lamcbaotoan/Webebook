using Newtonsoft.Json;
using System;
using System.Configuration;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Webebook.Helpers
{
    public static class MoMoHelper
    {
        public static string CreatePaymentUrl(string orderId, string orderInfo, decimal amount)
        {
            // Lấy thông tin từ Web.config
            string partnerCode = ConfigurationManager.AppSettings["Momo:PartnerCode"];
            string accessKey = ConfigurationManager.AppSettings["Momo:AccessKey"];
            string secretKey = ConfigurationManager.AppSettings["Momo:SecretKey"];
            string endpoint = ConfigurationManager.AppSettings["Momo:ApiEndpoint"];
            string returnUrl = ConfigurationManager.AppSettings["Momo:ReturnUrl"];
            string notifyUrl = ConfigurationManager.AppSettings["Momo:IpnUrl"]; // Đổi tên biến để khớp với MoMo
            string requestType = "captureWallet"; // Loại yêu cầu
            string extraData = ""; // Dữ liệu bổ sung, có thể để trống
            string requestId = Guid.NewGuid().ToString();
            string amountString = amount.ToString("F0");

            // *** SỬA LỖI QUAN TRỌNG: Sắp xếp lại chuỗi rawHash theo đúng tài liệu của MoMo ***
            string rawHash = $"partnerCode={partnerCode}" +
                             $"&accessKey={accessKey}" +
                             $"&requestId={requestId}" +
                             $"&amount={amountString}" +
                             $"&orderId={orderId}" +
                             $"&orderInfo={orderInfo}" +
                             $"&returnUrl={returnUrl}" +
                             $"&notifyUrl={notifyUrl}" + // Dùng notifyUrl
                             $"&extraData={extraData}";

            // Tạo chữ ký
            string signature = CreateSignature(rawHash, secretKey);

            // Tạo body cho request
            var requestBody = new
            {
                partnerCode,
                accessKey,
                requestId,
                amount = amountString,
                orderId,
                orderInfo,
                redirectUrl = returnUrl,
                ipnUrl = notifyUrl, // Gửi đi vẫn là ipnUrl
                requestType,
                lang = "vi",
                extraData,
                signature
            };

            string jsonRequest = JsonConvert.SerializeObject(requestBody);

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                HttpWebRequest httpWReq = (HttpWebRequest)WebRequest.Create(endpoint);
                httpWReq.Method = "POST";
                httpWReq.ContentType = "application/json; charset=utf-8";

                using (var streamWriter = new StreamWriter(httpWReq.GetRequestStream()))
                {
                    streamWriter.Write(jsonRequest);
                }

                HttpWebResponse response = (HttpWebResponse)httpWReq.GetResponse();
                using (var streamReader = new StreamReader(response.GetResponseStream()))
                {
                    string jsonResponse = streamReader.ReadToEnd();
                    var responseObject = JsonConvert.DeserializeObject<dynamic>(jsonResponse);
                    if (responseObject.resultCode != 0)
                    {
                        LogError($"MoMo Response Error: Code {responseObject.resultCode} - {responseObject.message}");
                        return null;
                    }
                    return responseObject.payUrl;
                }
            }
            catch (WebException webEx)
            {
                using (var stream = webEx.Response.GetResponseStream())
                using (var reader = new StreamReader(stream))
                {
                    LogError($"MoMo API Error Response: {reader.ReadToEnd()}");
                }
                return null;
            }
            catch (Exception ex)
            {
                LogError($"MoMo Request Error: {ex.Message}");
                return null;
            }
        }

        public static string CreateSignature(string message, string key)
        {
            byte[] keyByte = Encoding.UTF8.GetBytes(key);
            byte[] messageBytes = Encoding.UTF8.GetBytes(message);
            using (var hmacsha256 = new HMACSHA256(keyByte))
            {
                byte[] hashmessage = hmacsha256.ComputeHash(messageBytes);
                string hex = BitConverter.ToString(hashmessage);
                hex = hex.Replace("-", "").ToLower();
                return hex;
            }
        }

        private static void LogError(string message)
        {
            System.Diagnostics.Trace.TraceError($"MOMO_HELPER_ERROR: {message}");
        }
    }
}