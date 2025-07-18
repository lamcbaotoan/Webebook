using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Script.Serialization;
using System.Web.UI;
using System.Web.UI.WebControls;
using Webebook.Helpers;

namespace Webebook.WebForm.User
{
    public partial class thanhtoan : System.Web.UI.Page
    {
        string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;

        #region Properties
        public bool IsBuyNowMode
        {
            get { return ViewState["IsBuyNowMode"] as bool? ?? false; }
            set { ViewState["IsBuyNowMode"] = value; }
        }

        protected List<CartItemViewModel> SelectedItems
        {
            get { return ViewState["SelectedItems"] as List<CartItemViewModel>; }
            set { ViewState["SelectedItems"] = value; }
        }

        public decimal GrandTotal
        {
            get { return (ViewState["GrandTotal"] as decimal?) ?? 0m; }
            set { ViewState["GrandTotal"] = value; }
        }
        #endregion

        protected void Page_Load(object sender, EventArgs e)
        {
            if (Session["UserID"] == null)
            {
                Response.Redirect("~/WebForm/VangLai/dangnhap.aspx?returnUrl=" + Server.UrlEncode(Request.RawUrl), true);
                return;
            }

            if (ViewState["SelectedItems"] != null)
            {
                this.SelectedItems = ViewState["SelectedItems"] as List<CartItemViewModel>;
                this.GrandTotal = (ViewState["GrandTotal"] as decimal?) ?? 0;
            }

            if (!IsPostBack)
            {
                bool isBuyNowRequest = int.TryParse(Request.QueryString["buyNowId"], out int buyNowId) && buyNowId > 0;
                if (isBuyNowRequest)
                {
                    this.IsBuyNowMode = true;
                    LoadBuyNowItem(buyNowId);
                }
                else
                {
                    this.IsBuyNowMode = false;
                    if (Session["SelectedCartItems"] == null || !(Session["SelectedCartItems"] as List<int>).Any())
                    {
                        ShowSweetAlert("Lỗi", "Không có sản phẩm nào được chọn.", "warning", "~/WebForm/User/giohang_user.aspx");
                        DisableCheckout();
                        return;
                    }
                    LoadSelectedItems();
                }

                if (this.SelectedItems == null || !this.SelectedItems.Any())
                {
                    ShowSweetAlert("Lỗi", "Không tìm thấy thông tin sản phẩm.", "error", "~/WebForm/User/giohang_user.aspx");
                    DisableCheckout();
                }
                else
                {
                    BindDataAndDisplayTotal();
                    GenerateBankTransferContent();
                }
            }
        }

        protected void btnXacNhan_Click(object sender, EventArgs e)
        {
            if (Session["UserID"] == null) { ShowSweetAlert("Lỗi", "Phiên đăng nhập đã hết hạn.", "error"); return; }
            int userId = Convert.ToInt32(Session["UserID"]);

            if (this.SelectedItems == null || !this.SelectedItems.Any()) { ShowSweetAlert("Lỗi", "Không có sản phẩm để xử lý.", "error"); return; }

            var ownedBooks = CheckAlreadyOwnedOrPaidBooks(userId, this.SelectedItems.Select(i => i.IDSach).ToList());
            if (ownedBooks.Any())
            {
                ShowOwnedBooksAlert(ownedBooks);
                return;
            }

            string mainPaymentMethod = rblPaymentMethod.SelectedValue;
            string finalPaymentProvider = mainPaymentMethod;
            if (mainPaymentMethod == "E_WALLET")
            {
                finalPaymentProvider = rblEWalletProvider.SelectedValue;
            }

            // Bước 1: Luôn tạo đơn hàng với trạng thái Pending
            int orderId = CreatePendingOrder(userId, this.GrandTotal, finalPaymentProvider, this.SelectedItems);
            if (orderId <= 0)
            {
                ShowSweetAlert("Lỗi", "Không thể khởi tạo đơn hàng trong hệ thống.", "error");
                return;
            }

            // Bước 2: Chỉ xóa giỏ hàng ngay lập tức cho các phương thức thủ công
            if (finalPaymentProvider == "BANK_TRANSFER" || finalPaymentProvider == "CARD")
            {
                ClearCartItems(userId, this.SelectedItems);
            }

            // Xóa Session chứa các ID đã chọn để tránh đặt lại đơn hàng trùng lặp
            if (!this.IsBuyNowMode) { Session.Remove("SelectedCartItems"); }

            // Bước 3: Chuyển hướng người dùng
            switch (finalPaymentProvider)
            {
                case "VNPAY":
                    var httpContext = new HttpContextWrapper(HttpContext.Current);
                    string vnpayUrl = VnPayHelper.CreatePaymentUrl(orderId.ToString(), this.GrandTotal, httpContext);
                    if (!string.IsNullOrEmpty(vnpayUrl)) { Response.Redirect(vnpayUrl, false); }
                    else { DeletePendingOrder(orderId); ShowSweetAlert("Lỗi", "Không thể tạo yêu cầu thanh toán VNPAY.", "error"); }
                    break;
                case "MOMO":
                    string orderInfo = "Thanh toán đơn hàng #" + orderId;
                    string momoUrl = MoMoHelper.CreatePaymentUrl(orderId.ToString(), orderInfo, this.GrandTotal);
                    if (!string.IsNullOrEmpty(momoUrl)) { Response.Redirect(momoUrl, false); }
                    else { DeletePendingOrder(orderId); ShowSweetAlert("Lỗi", "Không thể tạo yêu cầu thanh toán MoMo.", "error"); }
                    break;
                default: // BANK_TRANSFER, CARD
                    Response.Redirect($"~/WebForm/User/xacnhandonhang.aspx?IDDonHang={orderId}", false);
                    break;
            }
        }

        #region Helper Methods

        private class OwnedBookResult
        {
            public string TenSach { get; set; }
            public string Status { get; set; }
        }

        private void GenerateBankTransferContent()
        {
            string randomCode = $"TTDH{new Random().Next(10000, 99999)}";
            lblBankTransferContent.Text = randomCode;
            string qrInfo = $"https://api.vietqr.io/image/970422-0376512695-print.png?amount={this.GrandTotal.ToString("F0")}&addInfo={HttpUtility.UrlEncode(randomCode)}&accountName=LAM CHU BAO TOAN";
            imgBankQR.ImageUrl = qrInfo;
        }

        private List<OwnedBookResult> CheckAlreadyOwnedOrPaidBooks(int userId, List<int> bookIds)
        {
            var ownedBooks = new List<OwnedBookResult>();
            if (!bookIds.Any()) return ownedBooks;

            string bookIdParams = string.Join(",", bookIds.Select((id, i) => $"@id{i}"));
            var cmd = new SqlCommand();
            var sqlBuilder = new StringBuilder($@"
                WITH FoundBooks AS (
                    SELECT s.IDSach, s.TenSach, 'Paid' AS Status, 2 AS Priority
                    FROM TuSach ts JOIN Sach s ON ts.IDSach = s.IDSach
                    WHERE ts.IDNguoiDung = @UserId AND ts.IDSach IN ({bookIdParams})
                    UNION ALL
                    SELECT s.IDSach, s.TenSach, dh.TrangThaiThanhToan,
                           CASE WHEN dh.TrangThaiThanhToan = 'Pending' THEN 1 ELSE 2 END AS Priority
                    FROM DonHang dh
                    JOIN ChiTietDonHang ctdh ON dh.IDDonHang = ctdh.IDDonHang
                    JOIN Sach s ON ctdh.IDSach = s.IDSach
                    WHERE dh.IDNguoiDung = @UserId AND s.IDSach IN ({bookIdParams})
                      AND dh.TrangThaiThanhToan IN ('Paid', 'Completed', 'Pending')
                ),
                RankedBooks AS (
                    SELECT TenSach, Status, ROW_NUMBER() OVER(PARTITION BY IDSach ORDER BY Priority ASC) as rn
                    FROM FoundBooks
                )
                SELECT TenSach, Status FROM RankedBooks WHERE rn = 1");

            cmd.Parameters.AddWithValue("@UserId", userId);
            for (int i = 0; i < bookIds.Count; i++)
            {
                cmd.Parameters.Add(new SqlParameter($"@id{i}", bookIds[i]));
            }

            using (var con = new SqlConnection(connectionString))
            {
                cmd.Connection = con;
                cmd.CommandText = sqlBuilder.ToString();
                try
                {
                    con.Open();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            ownedBooks.Add(new OwnedBookResult
                            {
                                TenSach = reader["TenSach"].ToString(),
                                Status = reader["Status"].ToString()
                            });
                        }
                    }
                }
                catch (Exception ex) { LogError($"CheckAlreadyOwnedOrPaidBooks Error: {ex.Message}"); }
            }
            return ownedBooks;
        }

        private int CreatePendingOrder(int userId, decimal total, string paymentMethod, List<CartItemViewModel> items)
        {
            int idDonHang = 0;
            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                var transaction = con.BeginTransaction();
                try
                {
                    var dhQuery = "INSERT INTO DonHang (IDNguoiDung, NgayDat, SoTien, TrangThaiThanhToan, PhuongThucThanhToan) OUTPUT INSERTED.IDDonHang VALUES (@IDNguoiDung, GETDATE(), @SoTien, 'Pending', @PhuongThuc)";
                    using (var cmdDH = new SqlCommand(dhQuery, con, transaction))
                    {
                        cmdDH.Parameters.AddWithValue("@IDNguoiDung", userId);
                        cmdDH.Parameters.AddWithValue("@SoTien", total);
                        cmdDH.Parameters.AddWithValue("@PhuongThuc", paymentMethod);
                        idDonHang = (int)cmdDH.ExecuteScalar();
                    }
                    if (idDonHang <= 0) throw new Exception("Không thể tạo bản ghi đơn hàng.");

                    var ctQuery = "INSERT INTO ChiTietDonHang (IDSach, IDDonHang, SoLuong, Gia) VALUES (@IDSach, @IDDonHang, @SoLuong, @Gia)";
                    foreach (var item in items)
                    {
                        using (var cmdCT = new SqlCommand(ctQuery, con, transaction))
                        {
                            cmdCT.Parameters.AddWithValue("@IDSach", item.IDSach);
                            cmdCT.Parameters.AddWithValue("@IDDonHang", idDonHang);
                            cmdCT.Parameters.AddWithValue("@SoLuong", item.SoLuong);
                            cmdCT.Parameters.AddWithValue("@Gia", item.DonGia);
                            cmdCT.ExecuteNonQuery();
                        }
                    }
                    transaction.Commit();
                    return idDonHang;
                }
                catch (Exception ex)
                {
                    if (transaction != null) transaction.Rollback();
                    LogError($"CreatePendingOrder Error: {ex.Message}");
                    return 0;
                }
            }
        }

        private void DeletePendingOrder(int orderId)
        {
            using (var con = new SqlConnection(connectionString))
            {
                con.Open();
                var transaction = con.BeginTransaction();
                try
                {
                    var cmdDetails = new SqlCommand("DELETE FROM ChiTietDonHang WHERE IDDonHang = @IDDonHang", con, transaction);
                    cmdDetails.Parameters.AddWithValue("@IDDonHang", orderId);
                    cmdDetails.ExecuteNonQuery();

                    var cmdOrder = new SqlCommand("DELETE FROM DonHang WHERE IDDonHang = @IDDonHang", con, transaction);
                    cmdOrder.Parameters.AddWithValue("@IDDonHang", orderId);
                    cmdOrder.ExecuteNonQuery();
                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    if (transaction != null) transaction.Rollback();
                    LogError($"DeletePendingOrder Error for OrderID {orderId}: {ex.Message}");
                }
            }
        }

        private void ClearCartItems(int userId, List<CartItemViewModel> items)
        {
            var cartItemIds = items.Where(i => i.IDGioHang > 0).Select(i => i.IDGioHang).ToList();
            if (!cartItemIds.Any()) return;
            using (var con = new SqlConnection(connectionString))
            {
                var deleteQuery = $"DELETE FROM GioHang WHERE IDNguoiDung = @UserId AND IDGioHang IN ({string.Join(",", cartItemIds.Select((id, i) => $"@IDGH{i}"))})";
                using (var cmdDelete = new SqlCommand(deleteQuery, con))
                {
                    cmdDelete.Parameters.AddWithValue("@UserId", userId);
                    for (int i = 0; i < cartItemIds.Count; i++)
                    {
                        cmdDelete.Parameters.AddWithValue($"@IDGH{i}", cartItemIds[i]);
                    }
                    try
                    {
                        con.Open();
                        cmdDelete.ExecuteNonQuery();
                        UpdateMasterCartCount();
                    }
                    catch (Exception ex) { LogError($"ClearCartItems Error: {ex.Message}"); }
                }
            }
        }

        private void LoadBuyNowItem(int sachId)
        {
            this.SelectedItems = new List<CartItemViewModel>();
            using (var con = new SqlConnection(connectionString))
            {
                string query = "SELECT TenSach, GiaSach FROM Sach WHERE IDSach = @IDSach";
                using (var cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@IDSach", sachId);
                    try
                    {
                        con.Open();
                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var item = new CartItemViewModel { IDGioHang = 0, IDSach = sachId, TenSach = reader["TenSach"].ToString(), SoLuong = 1, DonGia = Convert.ToDecimal(reader["GiaSach"]) };
                                this.SelectedItems.Add(item);
                                this.GrandTotal = item.ThanhTien;
                                ViewState["SelectedItems"] = this.SelectedItems;
                                ViewState["GrandTotal"] = this.GrandTotal;
                            }
                            else { DisableCheckout("Sách không tồn tại."); }
                        }
                    }
                    catch (Exception ex) { LogError($"LoadBuyNowItem Error for SachID {sachId}: {ex}"); DisableCheckout("Lỗi tải sách."); }
                }
            }
        }

        private void LoadSelectedItems()
        {
            var selectedCartItemIds = Session["SelectedCartItems"] as List<int>;
            if (selectedCartItemIds == null || !selectedCartItemIds.Any()) return;
            this.SelectedItems = new List<CartItemViewModel>();
            using (var con = new SqlConnection(connectionString))
            {
                var sqlBuilder = new StringBuilder("SELECT gh.IDGioHang, gh.IDSach, s.TenSach, gh.SoLuong, s.GiaSach FROM GioHang gh JOIN Sach s ON gh.IDSach = s.IDSach WHERE gh.IDNguoiDung = @UserId AND gh.IDGioHang IN (");
                var cmd = new SqlCommand();
                cmd.Parameters.AddWithValue("@UserId", Convert.ToInt32(Session["UserID"]));
                for (int i = 0; i < selectedCartItemIds.Count; i++)
                {
                    var paramName = "@IDGH" + i;
                    sqlBuilder.Append(paramName + (i < selectedCartItemIds.Count - 1 ? "," : ""));
                    cmd.Parameters.Add(new SqlParameter(paramName, selectedCartItemIds[i]));
                }
                sqlBuilder.Append(")");
                cmd.CommandText = sqlBuilder.ToString();
                cmd.Connection = con;
                try
                {
                    con.Open();
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            this.SelectedItems.Add(new CartItemViewModel { IDGioHang = Convert.ToInt32(reader["IDGioHang"]), IDSach = Convert.ToInt32(reader["IDSach"]), TenSach = reader["TenSach"].ToString(), SoLuong = Convert.ToInt32(reader["SoLuong"]), DonGia = Convert.ToDecimal(reader["GiaSach"]) });
                        }
                    }
                    this.GrandTotal = this.SelectedItems.Sum(i => i.ThanhTien);
                    ViewState["SelectedItems"] = this.SelectedItems;
                    ViewState["GrandTotal"] = this.GrandTotal;
                }
                catch (Exception ex) { LogError($"LoadSelectedItems Error: {ex}"); DisableCheckout("Lỗi tải giỏ hàng."); }
            }
        }

        private void BindDataAndDisplayTotal() { rptSelectedItems.DataSource = this.SelectedItems; rptSelectedItems.DataBind(); }
        private void DisableCheckout(string reason = "") { btnXacNhan.Enabled = false; btnXacNhan.ToolTip = reason; }
        protected string FormatCurrency(object price) { if (price == null || price == DBNull.Value) return "0 VNĐ"; return Convert.ToDecimal(price).ToString("N0", CultureInfo.GetCultureInfo("vi-VN")) + " VNĐ"; }
        private void UpdateMasterCartCount() { if (Master is UserMaster master) master.UpdateCartCount(); }
        private void LogError(string message) { System.Diagnostics.Trace.TraceError($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}"); }

        private void ShowSweetAlert(string title, string message, string icon, string redirectUrl = "")
        {
            string cleanMessage = new JavaScriptSerializer().Serialize(message);
            string script = $"Swal.fire({{ title: '{title}', html: {cleanMessage}, icon: '{icon}', confirmButtonText: 'Đã hiểu' }})";
            if (!string.IsNullOrEmpty(redirectUrl)) { script += $".then(() => {{ window.location.href = '{ResolveUrl(redirectUrl)}'; }})"; }
            script += ";";
            ScriptManager.RegisterStartupScript(this, this.GetType(), "SweetAlert" + Guid.NewGuid(), script, true);
        }

        private void ShowOwnedBooksAlert(List<OwnedBookResult> ownedBooks)
        {
            var serializer = new JavaScriptSerializer();
            bool hasPending = ownedBooks.Any(b => b.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
            string title = "Sách đã tồn tại";
            string confirmButtonText = hasPending ? "Tới Lịch sử mua hàng" : "Tới Tủ sách";
            string redirectUrl = hasPending ? ResolveUrl("~/WebForm/User/lichsumuahang.aspx") : ResolveUrl("~/WebForm/User/tusach.aspx");
            var htmlContent = new StringBuilder("<p class='text-left mb-2'>Các sách sau đã có trong tủ sách hoặc đang chờ thanh toán:</p><ul class='text-left list-disc list-inside bg-yellow-50 p-3 rounded-md border border-yellow-200'>");
            ownedBooks.ForEach(book => htmlContent.Append($"<li class='font-semibold'>{HttpUtility.HtmlEncode(book.TenSach)} <span class='text-xs font-normal text-gray-600'>({(book.Status == "Paid" || book.Status == "Completed" ? "Đã có" : "Đang chờ")})</span></li>"));
            htmlContent.Append("</ul>");
            string script = $@"Swal.fire({{ title: '{title}', html: {serializer.Serialize(htmlContent.ToString())}, icon: 'warning', showCancelButton: true, confirmButtonText: '{confirmButtonText}', cancelButtonText: 'Về Giỏ hàng' }}).then((result) => {{ if (result.isConfirmed) {{ window.location.href = '{redirectUrl}'; }} else {{ window.location.href = '{ResolveUrl("~/WebForm/User/giohang_user.aspx")}'; }} }});";
            ScriptManager.RegisterStartupScript(this, this.GetType(), "AlreadyOwnedAlert", script, true);
        }
        #endregion
    }

    [Serializable]
    public class CartItemViewModel
    {
        public int IDGioHang { get; set; }
        public int IDSach { get; set; }
        public string TenSach { get; set; }
        public int SoLuong { get; set; }
        public decimal DonGia { get; set; }
        public decimal ThanhTien => SoLuong * DonGia;
    }
}