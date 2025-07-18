using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Web.UI;
using System.Web.UI.WebControls;
using Webebook.Helpers;

namespace Webebook.WebForm.User
{
    public partial class xacnhandonhang : System.Web.UI.Page
    {
        string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                if (Request.QueryString["vnp_TxnRef"] != null)
                {
                    ProcessVnpayReturn();
                }
                else if (int.TryParse(Request.QueryString["IDDonHang"], out int orderId))
                {
                    LoadOrderConfirmation(orderId);
                }
                else
                {
                    ShowMessage("Không có thông tin đơn hàng để hiển thị.", isError: true);
                    pnlOrderDetails.Visible = false;
                }
            }
        }

        private void ProcessVnpayReturn()
        {
            VnPayLibrary vnpay = new VnPayLibrary();
            var vnpayData = Request.QueryString;
            foreach (string s in vnpayData)
            {
                if (!string.IsNullOrEmpty(s) && s.StartsWith("vnp_")) { vnpay.AddResponseData(s, vnpayData[s]); }
            }

            string vnp_HashSecret = ConfigurationManager.AppSettings["vnp_HashSecret"];
            bool checkSignature = vnpay.ValidateSignature(vnpay.GetResponseData("vnp_SecureHash"), vnp_HashSecret);

            if (!int.TryParse(vnpay.GetResponseData("vnp_TxnRef"), out int orderId))
            {
                ShowMessage("Lỗi: Thông tin trả về từ VNPay không hợp lệ.", true);
                pnlOrderDetails.Visible = false;
                return;
            }

            if (checkSignature)
            {
                string vnp_ResponseCode = vnpay.GetResponseData("vnp_ResponseCode");
                if (vnp_ResponseCode == "00")
                {
                    // Thanh toán thành công, gọi hàm xử lý ngay lập tức
                    ProcessSuccessfulOrder(orderId);
                }
                else
                {
                    // Thanh toán thất bại hoặc bị hủy, gọi hàm xử lý thất bại
                    ProcessFailedOrder(orderId);
                }
                // Luôn tải lại thông tin đơn hàng để hiển thị trạng thái cuối cùng
                LoadOrderConfirmation(orderId);
            }
            else
            {
                ShowMessage("Lỗi: Chữ ký không hợp lệ từ VNPay.", true);
                pnlOrderDetails.Visible = false;
            }
        }

        // *** LOGIC XỬ LÝ ĐƯỢC TẬP TRUNG TẠI ĐÂY ***

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

                    // 1. Kiểm tra đơn hàng có tồn tại và đang Pending không
                    string checkQuery = "SELECT IDNguoiDung FROM DonHang WHERE IDDonHang = @IDDonHang AND TrangThaiThanhToan = 'Pending'";
                    using (var cmdCheck = new SqlCommand(checkQuery, con, transaction))
                    {
                        cmdCheck.Parameters.AddWithValue("@IDDonHang", orderId);
                        object result = cmdCheck.ExecuteScalar();
                        if (result == null || result == DBNull.Value)
                        {
                            transaction.Rollback(); // Đơn hàng đã được xử lý rồi (ví dụ do IPN chạy trước)
                            return;
                        }
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

                    // 4. Xóa sách khỏi giỏ hàng
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
                    if (Master is UserMaster master) { master.UpdateCartCount(); }
                }
                catch (Exception ex)
                {
                    if (transaction != null) transaction.Rollback();
                    LogError($"ProcessSuccessfulOrder Error on Return Page for OrderID {orderId}: {ex}");
                }
            }
        }

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
                LogError($"ProcessFailedOrder Error for OrderID {orderId}: {ex}");
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

        private void LoadOrderConfirmation(int orderId)
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                string query = "SELECT NgayDat, SoTien, PhuongThucThanhToan, TrangThaiThanhToan, IDNguoiDung FROM DonHang WHERE IDDonHang = @IDDonHang";
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@IDDonHang", orderId);
                    try
                    {
                        con.Open();
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                int orderUserId = Convert.ToInt32(reader["IDNguoiDung"]);
                                int currentUserId = (Session["UserID"] != null) ? Convert.ToInt32(Session["UserID"]) : -1;

                                if (orderUserId != currentUserId)
                                {
                                    ShowMessage("Bạn không có quyền xem đơn hàng này.", isError: true);
                                    pnlOrderDetails.Visible = false;
                                    return;
                                }

                                // Hiển thị thông tin chung của đơn hàng
                                lblIDDonHang.Text = orderId.ToString();
                                lblNgayDat.Text = Convert.ToDateTime(reader["NgayDat"]).ToString("dd/MM/yyyy HH:mm");
                                lblTongTien.Text = Convert.ToDecimal(reader["SoTien"]).ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " VNĐ";
                                lblPhuongThuc.Text = GetFriendlyPaymentMethodName(reader["PhuongThucThanhToan"].ToString());

                                string status = reader["TrangThaiThanhToan"].ToString();
                                string paymentMethod = reader["PhuongThucThanhToan"].ToString();

                                // *** SỬA LỖI LOGIC HIỂN THỊ ***
                                bool isManualPayment = (paymentMethod == "BANK_TRANSFER" || paymentMethod == "CARD");

                                if (status.Equals("Completed", StringComparison.OrdinalIgnoreCase) || status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Trường hợp 1: Đã thanh toán thành công
                                    pnlSuccessIcon.Visible = true;
                                    pnlFailureIcon.Visible = false;
                                    lblOrderStatusTitle.Text = "Thanh Toán Thành Công!";
                                    lblOrderStatusMessage.Text = "Cảm ơn bạn! Sách đã được thêm vào tủ sách của bạn.";
                                    hlViewHistory.NavigateUrl = "~/WebForm/User/tusach.aspx";
                                    hlViewHistory.Text = "<i class='fas fa-book mr-2'></i> Vào Tủ Sách";
                                    hlViewHistory.Visible = true;
                                }
                                else if (status.Equals("Pending", StringComparison.OrdinalIgnoreCase) && isManualPayment)
                                {
                                    // Trường hợp 2: Đặt hàng thủ công thành công, chờ thanh toán
                                    pnlSuccessIcon.Visible = true;
                                    pnlFailureIcon.Visible = false;
                                    lblOrderStatusTitle.Text = "Đặt Hàng Thành Công!";
                                    lblOrderStatusMessage.Text = "Đơn hàng của bạn đã được ghi nhận. Vui lòng thanh toán để hoàn tất.";
                                    hlViewHistory.Visible = true;
                                }
                                else // Trường hợp còn lại: Pending (online) hoặc Failed
                                {
                                    pnlSuccessIcon.Visible = false;
                                    pnlFailureIcon.Visible = true;
                                    lblOrderStatusTitle.Text = "Đơn Hàng Chưa Hoàn Tất";
                                    lblOrderStatusMessage.Text = "Đơn hàng đang chờ xử lý hoặc đã thất bại. Vui lòng kiểm tra Lịch sử mua hàng.";
                                    hlViewHistory.Visible = true;
                                }
                                pnlOrderDetails.Visible = true;
                            }
                            else
                            {
                                ShowMessage("Không tìm thấy thông tin đơn hàng.", isError: true);
                                pnlOrderDetails.Visible = false;
                            }
                        }
                    }
                    catch (Exception ex) { LogError($"Load Order Confirmation Error (ID: {orderId}): {ex}"); ShowMessage("Lỗi khi tải thông tin đơn hàng.", isError: true); pnlOrderDetails.Visible = false; }
                }
            }
        }

        private string GetFriendlyPaymentMethodName(string paymentMethodCode)
        {
            switch (paymentMethodCode?.ToUpper())
            {
                case "VNPAY":
                    return "Cổng VNPAY (Thẻ/QR Pay)";
                case "MOMO":
                    return "Ví điện tử MoMo";
                case "BANK_TRANSFER":
                    return "Chuyển khoản ngân hàng (Thủ công)";
                default:
                    return paymentMethodCode;
            }
        }

        private void ShowMessage(string message, bool isError)
        {
            lblMessage.Text = message;
            lblMessage.CssClass = "block mb-6 text-center text-lg font-medium p-4 rounded-md border " +
                                  (isError ? "bg-red-50 border-red-300 text-red-700" : "bg-green-50 border-green-300 text-green-700");
            lblMessage.Visible = true;
        }

        private void LogError(string message) { System.Diagnostics.Trace.TraceError(message); }
    }
}