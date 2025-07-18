// File: /WebForm/User/MomoIpn.aspx.cs
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using Webebook.Helpers;

namespace Webebook.WebForm.User
{
    public partial class MomoIpn : System.Web.UI.Page
    {
        private static readonly string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;

        protected void Page_Load(object sender, EventArgs e)
        {
            string jsonResponse = "";
            using (var reader = new StreamReader(Request.InputStream))
            {
                jsonResponse = reader.ReadToEnd();
            }

            if (string.IsNullOrEmpty(jsonResponse))
            {
                LogError("MoMo IPN: Received empty request body.");
                return;
            }

            try
            {
                dynamic data = JsonConvert.DeserializeObject(jsonResponse);

                string partnerCode = data.partnerCode;
                string orderIdStr = data.orderId;
                string requestId = data.requestId;
                long amount = data.amount;
                string orderInfo = data.orderInfo;
                string orderType = data.orderType;
                long transId = data.transId;
                int resultCode = data.resultCode;
                string message = data.message;
                string payType = data.payType;
                long responseTime = data.responseTime;
                string extraData = data.extraData;
                string signature = data.signature;

                string secretKey = ConfigurationManager.AppSettings["Momo:SecretKey"];
                string accessKey = ConfigurationManager.AppSettings["Momo:AccessKey"];

                string rawHash = $"accessKey={accessKey}&amount={amount}&extraData={extraData}&message={message}&orderId={orderIdStr}&orderInfo={orderInfo}&orderType={orderType}&partnerCode={partnerCode}&payType={payType}&requestId={requestId}&responseTime={responseTime}&resultCode={resultCode}&transId={transId}";

                string mySignature = MoMoHelper.CreateSignature(rawHash, secretKey);

                if (mySignature == signature)
                {
                    Log($"MoMo IPN: Signature valid for OrderID {orderIdStr}. ResultCode: {resultCode}");
                    if (int.TryParse(orderIdStr, out int orderId))
                    {
                        if (resultCode == 0) // Giao dịch thành công
                        {
                            ProcessSuccessfulOrder(int.Parse(orderIdStr));
                        }
                        else // Giao dịch thất bại
                        {
                            ProcessFailedOrder(int.Parse(orderIdStr));
                        }
                    }
                    else
                    {
                        LogError($"MoMo IPN: Invalid OrderID format: {orderIdStr}");
                    }
                }
                else
                {
                    LogError($"MoMo IPN: Invalid Signature for OrderID {orderIdStr}. Expected: {mySignature}, Got: {signature}");
                }
            }
            catch (Exception ex)
            {
                LogError($"MoMo IPN: Error parsing request body. JSON: {jsonResponse}. Error: {ex.Message}");
            }
            // MoMo không yêu cầu trả về nội dung, chỉ cần HTTP Status 204 (No Content) là đủ, việc không Response.Write gì cả sẽ mặc định làm điều đó.
        }

        private void ProcessSuccessfulOrder(int orderId)
        {
            Log($"Starting to process successful MoMo order ID: {orderId}");
            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                var transaction = con.BeginTransaction();
                try
                {
                    int userId = 0;
                    var bookIds = new List<int>();

                    // 1. Kiểm tra đơn hàng
                    string checkQuery = "SELECT IDNguoiDung FROM DonHang WHERE IDDonHang = @IDDonHang AND TrangThaiThanhToan = 'Pending'";
                    using (var cmdCheck = new SqlCommand(checkQuery, con, transaction))
                    {
                        cmdCheck.Parameters.AddWithValue("@IDDonHang", orderId);
                        object result = cmdCheck.ExecuteScalar();
                        if (result == null || result == DBNull.Value) { transaction.Rollback(); return; }
                        userId = Convert.ToInt32(result);
                    }

                    // 2. Cập nhật trạng thái đơn hàng thành 'Completed'
                    string updateQuery = "UPDATE DonHang SET TrangThaiThanhToan = 'Completed' WHERE IDDonHang = @IDDonHang";
                    using (var cmdUpdate = new SqlCommand(updateQuery, con, transaction))
                    {
                        cmdUpdate.Parameters.AddWithValue("@IDDonHang", orderId);
                        cmdUpdate.ExecuteNonQuery();
                    }

                    // 3. Lấy danh sách sách và thêm vào tủ sách
                    string itemsQuery = "SELECT IDSach FROM ChiTietDonHang WHERE IDDonHang = @IDDonHang";
                    using (var cmdItems = new SqlCommand(itemsQuery, con, transaction))
                    {
                        cmdItems.Parameters.AddWithValue("@IDDonHang", orderId);
                        using (var reader = cmdItems.ExecuteReader())
                        {
                            while (reader.Read()) { bookIds.Add(Convert.ToInt32(reader["IDSach"])); }
                        }
                    }

                    foreach (int bookId in bookIds)
                    {
                        AddBookToBookshelf(userId, bookId, con, transaction);
                    }

                    // 4. *** THÊM MỚI: Xóa các sách đã mua khỏi giỏ hàng ***
                    if (bookIds.Any())
                    {
                        string deleteCartQuery = $"DELETE FROM GioHang WHERE IDNguoiDung = @IDNguoiDung AND IDSach IN ({string.Join(",", bookIds)})";
                        using (var cmdDelete = new SqlCommand(deleteCartQuery, con, transaction))
                        {
                            cmdDelete.Parameters.AddWithValue("@IDNguoiDung", userId);
                            cmdDelete.ExecuteNonQuery();
                            Log($"MoMo: Deleted items from cart for UserID {userId}.");
                        }
                    }

                    transaction.Commit();
                    Log($"MoMo: Transaction committed successfully for OrderID {orderId}.");
                }
                catch (Exception ex)
                {
                    if (transaction != null) transaction.Rollback();
                    LogError($"MoMo IPN Success Process Error for OrderID {orderId}: {ex.ToString()}");
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

        private void ProcessFailedOrder(int orderId)
        {
            Log($"Processing failed MoMo order ID: {orderId}");
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
                LogError($"MoMo IPN Failed Process Error for OrderID {orderId}: {ex.ToString()}");
            }
        }

        private void Log(string message)
        {
            System.Diagnostics.Trace.WriteLine($"MOMO_IPN_LOG: {message}");
        }
        private void LogError(string message)
        {
            System.Diagnostics.Trace.TraceError($"MOMO_IPN_ERROR: {message}");
        }
    }
}