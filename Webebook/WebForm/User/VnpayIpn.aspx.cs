using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Web.UI;
using Webebook.Helpers;

namespace Webebook.WebForm.User
{
    public partial class VnpayIpn : Page
    {
        private static readonly string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;

        protected void Page_Load(object sender, EventArgs e)
        {
            Log("IPN URL was hit.");
            if (Request.QueryString.Count > 0)
            {
                string vnp_HashSecret = ConfigurationManager.AppSettings["vnp_HashSecret"];
                var vnpayData = Request.QueryString;
                VnPayLibrary vnpay = new VnPayLibrary();

                foreach (string s in vnpayData)
                {
                    if (!string.IsNullOrEmpty(s) && s.StartsWith("vnp_"))
                    {
                        vnpay.AddResponseData(s, vnpayData[s]);
                    }
                }

                if (!long.TryParse(vnpay.GetResponseData("vnp_TxnRef"), out long orderId))
                {
                    LogError("IPN Error: Invalid vnp_TxnRef");
                    Response.Write("{\"RspCode\":\"01\",\"Message\":\"Order not found\"}");
                    return;
                }

                string vnp_SecureHash = Request.QueryString["vnp_SecureHash"];
                bool checkSignature = vnpay.ValidateSignature(vnp_SecureHash, vnp_HashSecret);

                if (checkSignature)
                {
                    Log($"IPN for OrderID {orderId} has a VALID signature.");
                    string vnp_ResponseCode = vnpay.GetResponseData("vnp_ResponseCode");
                    string vnp_TransactionStatus = vnpay.GetResponseData("vnp_TransactionStatus");

                    if (vnp_ResponseCode == "00" && vnp_TransactionStatus == "00")
                    {
                        ProcessSuccessfulOrder((int)orderId);
                        // Trả về cho VNPay để xác nhận đã nhận IPN
                        Response.Write("{\"RspCode\":\"00\",\"Message\":\"Confirm Success\"}");
                    }
                    else
                    {
                        // Thanh toán thất bại hoặc bị hủy
                        ProcessFailedOrder((int)orderId);
                        Response.Write("{\"RspCode\":\"00\",\"Message\":\"Confirm Success\"}");
                    }
                }
                else
                {
                    LogError($"IPN Invalid Signature for OrderID {orderId}");
                    Response.Write("{\"RspCode\":\"97\",\"Message\":\"Invalid Signature\"}");
                }
            }
        }




        private void ProcessSuccessfulOrder(int orderId)
        {
            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                var transaction = con.BeginTransaction();
                try
                {
                    int userId = 0;
                    var bookIds = new List<int>();

                    // 1. Kiểm tra đơn hàng có tồn tại và đang Pending không, lấy ra UserID
                    string checkQuery = "SELECT IDNguoiDung FROM DonHang WHERE IDDonHang = @IDDonHang AND TrangThaiThanhToan = 'Pending'";
                    using (var cmdCheck = new SqlCommand(checkQuery, con, transaction))
                    {
                        cmdCheck.Parameters.AddWithValue("@IDDonHang", orderId);
                        object result = cmdCheck.ExecuteScalar();
                        if (result == null || result == DBNull.Value)
                        {
                            transaction.Rollback();
                            return; // Đơn hàng không tồn tại hoặc đã được xử lý
                        }
                        userId = Convert.ToInt32(result);
                    }

                    // 2. SỬA LẠI: Cập nhật trạng thái đơn hàng thành 'Completed'
                    string updateQuery = "UPDATE DonHang SET TrangThaiThanhToan = 'Completed' WHERE IDDonHang = @IDDonHang";
                    using (var cmdUpdate = new SqlCommand(updateQuery, con, transaction))
                    {
                        cmdUpdate.Parameters.AddWithValue("@IDDonHang", orderId);
                        cmdUpdate.ExecuteNonQuery();
                    }

                    // 3. Lấy danh sách sách đã mua
                    string itemsQuery = "SELECT IDSach FROM ChiTietDonHang WHERE IDDonHang = @IDDonHang";
                    using (var cmdItems = new SqlCommand(itemsQuery, con, transaction))
                    {
                        cmdItems.Parameters.AddWithValue("@IDDonHang", orderId);
                        using (var reader = cmdItems.ExecuteReader())
                        {
                            while (reader.Read()) { bookIds.Add(Convert.ToInt32(reader["IDSach"])); }
                        }
                    }

                    // 4. Thêm sách vào tủ sách của người dùng
                    foreach (int bookId in bookIds)
                    {
                        AddBookToBookshelf(userId, bookId, con, transaction);
                    }

                    // 5. THÊM MỚI: Xóa các sách đã mua khỏi giỏ hàng
                    if (bookIds.Any())
                    {
                        string deleteCartQuery = $"DELETE FROM GioHang WHERE IDNguoiDung = @IDNguoiDung AND IDSach IN ({string.Join(",", bookIds)})";
                        using (var cmdDelete = new SqlCommand(deleteCartQuery, con, transaction))
                        {
                            cmdDelete.Parameters.AddWithValue("@IDNguoiDung", userId);
                            cmdDelete.ExecuteNonQuery();
                        }
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    if (transaction != null) transaction.Rollback();
                    LogError($"IPN Success Process Error for OrderID {orderId}: {ex.ToString()}");
                }
            }
        }

        private void AddBookToBookshelf(int userId, int sachId, SqlConnection con, SqlTransaction tran)
        {
            string query = "IF NOT EXISTS (SELECT 1 FROM TuSach WHERE IDNguoiDung = @IDNguoiDung AND IDSach = @IDSach) BEGIN INSERT INTO TuSach (IDNguoiDung, IDSach, NgayThem) VALUES (@IDNguoiDung, @IDSach, GETDATE()) END";
            using (SqlCommand cmd = new SqlCommand(query, con, tran))
            {
                cmd.Parameters.AddWithValue("@IDNguoiDung", userId);
                cmd.Parameters.AddWithValue("@IDSach", sachId);
                cmd.ExecuteNonQuery();
            }
        }

        // *** BẮT ĐẦU PHẦN CODE ĐƯỢC HOÀN THIỆN ***
        private void ProcessFailedOrder(int orderId)
        {
            try
            {
                using (var con = new SqlConnection(connectionString))
                {
                    string updateQuery = "UPDATE DonHang SET TrangThaiThanhToan = 'Failed' WHERE IDDonHang = @IDDonHang AND TrangThaiThanhToan = 'Pending'";
                    using (var cmdUpdate = new SqlCommand(updateQuery, con))
                    {
                        cmdUpdate.Parameters.AddWithValue("@IDDonHang", orderId);
                        con.Open();
                        cmdUpdate.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"IPN Failed Process Error for OrderID {orderId}: {ex.ToString()}");
            }
        }
        // *** KẾT THÚC PHẦN CODE ĐƯỢC HOÀN THIỆN ***

        private void Log(string message)
        {
            System.Diagnostics.Trace.WriteLine($"VNPAY_IPN_LOG: {message}");
        }
        private void LogError(string message)
        {
            System.Diagnostics.Trace.TraceError($"VNPAY_IPN_ERROR: {message}");
        }
    }
}