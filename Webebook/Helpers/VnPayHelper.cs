// File: /Helpers/VnPayHelper.cs
using System;
using System.Configuration;
using System.Web;
// Thư viện VnPayLibrary.cs của bạn
using Webebook.Helpers;

// SỬA LẠI NAMESPACE CHO ĐÚNG VỚI PROJECT CỦA BẠN NẾU CẦN
namespace Webebook.Helpers
{
    public static class VnPayHelper
    {
        public static string CreatePaymentUrl(string orderId, decimal amount, HttpContextBase httpContext)
        {
            // Lấy các thông tin cấu hình từ Web.config
           // string vnp_Returnurl = ConfigurationManager.AppSettings["vnp_ReturnUrl"];
            string vnp_Url = ConfigurationManager.AppSettings["vnp_Url"];
            string vnp_TmnCode = ConfigurationManager.AppSettings["vnp_TmnCode"];
            string vnp_HashSecret = ConfigurationManager.AppSettings["vnp_HashSecret"];


            // *** SỬA LỖI: Xóa dòng đọc URL từ config và tạo tự động ***
            string baseUrl = $"{httpContext.Request.Url.Scheme}://{httpContext.Request.Url.Authority}";
            string vnp_Returnurl = $"{baseUrl}/WebForm/User/xacnhandonhang.aspx";
            // *** BẮT ĐẦU SỬA LỖI MÚI GIỜ ***

            // Lấy múi giờ của Việt Nam (SE Asia Standard Time tương ứng với GMT+7)
            TimeZoneInfo vnTimeZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

            // Lấy thời gian hiện tại theo giờ UTC và chuyển đổi sang giờ Việt Nam
            DateTime vnTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, vnTimeZone);

            // *** KẾT THÚC SỬA LỖI MÚI GIỜ ***

            VnPayLibrary vnpay = new VnPayLibrary();
            vnpay.AddRequestData("vnp_Version", "2.1.0");
            vnpay.AddRequestData("vnp_Command", "pay");
            vnpay.AddRequestData("vnp_TmnCode", vnp_TmnCode);
            vnpay.AddRequestData("vnp_Amount", ((long)amount * 100).ToString());

            // Sử dụng thời gian đã được chuyển đổi sang giờ Việt Nam
            vnpay.AddRequestData("vnp_CreateDate", vnTime.ToString("yyyyMMddHHmmss"));

            vnpay.AddRequestData("vnp_CurrCode", "VND");
            vnpay.AddRequestData("vnp_IpAddr", GetIpAddress(httpContext));
            vnpay.AddRequestData("vnp_Locale", "vn");
            vnpay.AddRequestData("vnp_OrderInfo", "Thanh toan don hang " + orderId);
            vnpay.AddRequestData("vnp_OrderType", "other");
            vnpay.AddRequestData("vnp_ReturnUrl", vnp_Returnurl);
            vnpay.AddRequestData("vnp_TxnRef", orderId);

            string paymentUrl = vnpay.CreateRequestUrl(vnp_Url, vnp_HashSecret);
            return paymentUrl;
        }

        public static string GetIpAddress(HttpContextBase httpContext)
        {
            string ipAddress = httpContext.Request.ServerVariables["HTTP_X_FORWARDED_FOR"];
            if (string.IsNullOrEmpty(ipAddress))
            {
                ipAddress = httpContext.Request.ServerVariables["REMOTE_ADDR"];
            }
            return ipAddress;
        }
    }
}