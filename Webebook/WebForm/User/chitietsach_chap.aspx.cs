using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using System.Globalization;

namespace Webebook.WebForm.User
{
    public partial class chitietsach_chap : System.Web.UI.Page
    {
        private readonly string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"]?.ConnectionString;
        private int userId = 0;
        private int sachId = 0;

        protected void Page_Load(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                ShowMessage("Lỗi cấu hình hệ thống. Không thể kết nối cơ sở dữ liệu.", true);
                DisableContentPanels();
                return;
            }

            if (Session["UserID"] == null || !int.TryParse(Session["UserID"].ToString(), out userId) || userId <= 0)
            {
                string returnUrl = Server.UrlEncode(Request.Url.PathAndQuery);
                Response.Redirect(ResolveUrl("~/WebForm/VangLai/dangnhap.aspx") + "?returnUrl=" + returnUrl, false);
                Context.ApplicationInstance.CompleteRequest();
                return;
            }

            if (!int.TryParse(Request.QueryString["IDSach"], out sachId) || sachId <= 0)
            {
                ShowMessage("ID Sách không hợp lệ hoặc không được cung cấp.", true);
                DisableContentPanels();
                hlBackToBookshelf.Visible = false;
                return;
            }

            if (!CheckIfBookIsInBookshelf())
            {
                ShowMessage("Bạn không có quyền truy cập sách này. Đang chuyển hướng về Tủ sách sau 5 giây...", true);
                DisableContentPanels();
                string redirectUrl = ResolveUrl("~/WebForm/User/tusach.aspx") + "?error=notowned";
                string script = $"setTimeout(function() {{ window.location.href = '{redirectUrl}'; }}, 5000);";
                ScriptManager.RegisterStartupScript(this, this.GetType(), "RedirectUser", script, true);
                return;
            }

            if (!IsPostBack)
            {
                try
                {
                    LoadBookDetails();
                    if (pnlBookDetails.Visible)
                    {
                        LoadChapterList();
                        LoadContinueButton();
                        LoadBookComments();
                    }
                    UpdateMasterCartCount();
                }
                catch (Exception ex)
                {
                    ShowMessage("Đã xảy ra lỗi không mong muốn khi tải trang.", true);
                    DisableContentPanels();
                    System.Diagnostics.Trace.TraceError($"Fatal Page_Load error (User, IDSach={sachId}, UserID={userId}): {ex}");
                }
            }

            if (IsPostBack && lblMessage.Visible)
            {
                lblMessage.Visible = false;
            }
        }

        private bool CheckIfBookIsInBookshelf()
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                string query = "SELECT COUNT(1) FROM TuSach WHERE IDNguoiDung = @IDNguoiDung AND IDSach = @IDSach";
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@IDNguoiDung", this.userId);
                    cmd.Parameters.AddWithValue("@IDSach", this.sachId);
                    try
                    {
                        con.Open();
                        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                    }
                    catch (Exception ex)
                    {
                        LogErrorAndShowMessage($"Lỗi khi kiểm tra sách trong tủ sách (UserID={this.userId}, IDSach={this.sachId}): {ex}", "Đã xảy ra lỗi khi xác thực quyền truy cập sách.");
                        return false;
                    }
                }
            }
        }

        private void LoadBookDetails()
        {
            if (pnlBookDetails == null || lblTenSach == null)
            {
                LogErrorAndShowMessage("Lỗi giao diện: Thiếu control chi tiết sách.", "Giao diện trang bị lỗi. Vui lòng liên hệ quản trị viên.");
                DisableContentPanels();
                return;
            }

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                string query = @"SELECT
                                    s.TenSach, s.TacGia, s.MoTa, s.DuongDanBiaSach,
                                    ISNULL(s.LoaiSach, 'N/A') AS LoaiSach,
                                    ISNULL(s.NhaXuatBan, 'N/A') AS NhaXuatBan,
                                    ISNULL(s.NhomDich, 'N/A') AS NhomDich,
                                    ISNULL(s.TrangThaiNoiDung, 'Đang cập nhật') AS TrangThaiNoiDung,
                                    ISNULL(s.TheLoaiChuoi, '') AS TheLoaiChuoi
                                FROM Sach s
                                WHERE s.IDSach = @IDSach";
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@IDSach", sachId);
                    try
                    {
                        con.Open();
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                string tenSach = reader["TenSach"].ToString();
                                lblTenSach.Text = HttpUtility.HtmlEncode(tenSach);
                                Page.Title = "Chi tiết: " + tenSach;
                                lblTacGia.Text = HttpUtility.HtmlEncode(reader["TacGia"]?.ToString() ?? "Chưa cập nhật");
                                lblMoTa.Text = FormatDescription(reader["MoTa"]?.ToString());
                                imgBiaSach.ImageUrl = GetImageUrl(reader["DuongDanBiaSach"]);
                                imgBiaSach.AlternateText = "Bìa sách " + HttpUtility.HtmlEncode(tenSach);

                                lblLoaiSach.Text = HttpUtility.HtmlEncode(reader["LoaiSach"].ToString());
                                lblNhaXuatBan.Text = HttpUtility.HtmlEncode(reader["NhaXuatBan"].ToString());
                                lblNhomDich.Text = HttpUtility.HtmlEncode(reader["NhomDich"].ToString());
                                lblTrangThai.Text = HttpUtility.HtmlEncode(reader["TrangThaiNoiDung"].ToString());

                                string genresString = reader["TheLoaiChuoi"].ToString();
                                List<string> genres = genresString.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                                                .Select(g => HttpUtility.HtmlEncode(g.Trim()))
                                                                .Where(g => !string.IsNullOrEmpty(g))
                                                                .ToList();
                                if (genres.Any())
                                {
                                    rptGenres.DataSource = genres;
                                    rptGenres.DataBind();
                                    rptGenres.Visible = true;
                                    lblNoGenres.Visible = false;
                                }
                                else
                                {
                                    rptGenres.Visible = false;
                                    lblNoGenres.Visible = true;
                                }
                                pnlBookDetails.Visible = true;
                            }
                            else
                            {
                                ShowMessage("Không tìm thấy thông tin chi tiết cho sách này (ID: " + sachId + ").", true);
                                DisableContentPanels();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogErrorAndShowMessage($"Lỗi LoadBookDetails (User, IDSach={sachId}): {ex}", "Đã xảy ra lỗi khi tải chi tiết sách.");
                        DisableContentPanels();
                    }
                }
            }
        }

        private void LoadChapterList()
        {
            // Implementation remains the same
            if (rptChapters == null || pnlChapterList == null || lblNoChapters == null) return;
            DataTable dt = new DataTable();
            using (var con = new SqlConnection(connectionString))
            {
                string query = "SELECT IDSach, SoChuong, TenChuong FROM NoiDungSach WHERE IDSach = @IDSach ORDER BY SoChuong ASC";
                using (var cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@IDSach", sachId);
                    try
                    {
                        con.Open();
                        var da = new SqlDataAdapter(cmd);
                        da.Fill(dt);
                        rptChapters.DataSource = dt;
                        rptChapters.DataBind();
                        pnlChapterList.Visible = dt.Rows.Count > 0;
                        lblNoChapters.Visible = dt.Rows.Count == 0;
                    }
                    catch (Exception ex)
                    {
                        LogErrorAndShowMessage($"Lỗi LoadChapterList (User, IDSach={sachId}): {ex}", "Lỗi khi tải danh sách chương.");
                        pnlChapterList.Visible = false;
                        lblNoChapters.Visible = true;
                    }
                }
            }
        }

        private void LoadContinueButton()
        {
            // Implementation remains the same
            if (hlReadContinue == null) return;
            string viTriDoc = null;
            int totalChapters = 0, firstChapter = 0;

            using (var con = new SqlConnection(connectionString))
            {
                try
                {
                    con.Open();
                    var queryViTriDoc = "SELECT ViTriDoc FROM TuSach WHERE IDNguoiDung = @UserId AND IDSach = @IDSach";
                    using (var cmdViTri = new SqlCommand(queryViTriDoc, con))
                    {
                        cmdViTri.Parameters.AddWithValue("@UserId", userId);
                        cmdViTri.Parameters.AddWithValue("@IDSach", sachId);
                        object resultViTri = cmdViTri.ExecuteScalar();
                        if (resultViTri != null && resultViTri != DBNull.Value && !string.IsNullOrWhiteSpace(resultViTri.ToString()) && resultViTri.ToString() != "0")
                        {
                            viTriDoc = resultViTri.ToString();
                        }
                    }
                    var queryCounts = "SELECT COUNT(DISTINCT SoChuong), MIN(SoChuong) FROM NoiDungSach WHERE IDSach = @IDSach";
                    using (var cmdCounts = new SqlCommand(queryCounts, con))
                    {
                        cmdCounts.Parameters.AddWithValue("@IDSach", sachId);
                        using (var reader = cmdCounts.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                if (reader[0] != DBNull.Value) totalChapters = Convert.ToInt32(reader[0]);
                                if (reader[1] != DBNull.Value) firstChapter = Convert.ToInt32(reader[1]);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogErrorAndShowMessage($"Lỗi LoadContinueButton (User, IDSach={sachId}, UserID={userId}): {ex}", "Lỗi khi kiểm tra tiến độ đọc.");
                    hlReadContinue.Visible = false;
                    return;
                }
            }

            string buttonText, navigateUrl = "#";
            string cssClass = hlReadContinue.CssClass.Replace(" disabled", "").Trim();
            if (totalChapters == 0 || firstChapter == 0)
            {
                buttonText = "<i class='fas fa-book mr-2'></i> Chưa có nội dung";
                cssClass += " disabled";
            }
            else if (string.IsNullOrEmpty(viTriDoc))
            {
                buttonText = $"<i class='fas fa-book-open mr-2'></i> Bắt đầu đọc (Chương {firstChapter})";
                navigateUrl = ResolveUrl($"~/WebForm/User/docsach.aspx?IDSach={sachId}&SoChuong={firstChapter}");
            }
            else
            {
                buttonText = $"<i class='fas fa-play mr-2'></i> Tiếp tục đọc (Chương {HttpUtility.HtmlEncode(viTriDoc)})";
                navigateUrl = ResolveUrl($"~/WebForm/User/docsach.aspx?IDSach={sachId}&SoChuong={HttpUtility.UrlEncode(viTriDoc)}");
            }
            hlReadContinue.Text = buttonText;
            hlReadContinue.NavigateUrl = navigateUrl;
            hlReadContinue.CssClass = cssClass;
            hlReadContinue.Visible = true;
        }

        private void LoadBookComments()
        {
            // Implementation remains the same
            if (rptBookComments == null || lblNoBookComments == null) return;
            var dtComments = new DataTable();
            using (var con = new SqlConnection(connectionString))
            {
                string query = @"SELECT TOP 20 t.IDNguoiDung, t.SoChap, t.BinhLuan, t.NgayBinhLuan,
                                    ISNULL(nd.Ten, nd.Username) AS TenHienThi, nd.AnhNen
                                FROM TuongTac t
                                LEFT JOIN NguoiDung nd ON t.IDNguoiDung = nd.IDNguoiDung
                                WHERE t.IDSach = @IDSach AND t.BinhLuan IS NOT NULL AND LTRIM(RTRIM(t.BinhLuan)) <> ''
                                ORDER BY t.NgayBinhLuan DESC";
                using (var cmd = new SqlCommand(query, con))
                {
                    cmd.Parameters.AddWithValue("@IDSach", sachId);
                    try
                    {
                        con.Open();
                        new SqlDataAdapter(cmd).Fill(dtComments);
                        rptBookComments.DataSource = dtComments;
                        rptBookComments.DataBind();
                        lblNoBookComments.Visible = (dtComments.Rows.Count == 0);
                    }
                    catch (Exception ex)
                    {
                        LogErrorAndShowMessage($"Lỗi LoadBookComments (User, IDSach={sachId}): {ex}", "Lỗi khi tải bình luận.");
                        lblNoBookComments.Visible = true;
                    }
                }
            }
        }

        #region Helper Functions (No changes needed)
        protected string GetImageUrl(object pathData)
        {
            string placeholder = ResolveUrl("~/Images/placeholder_cover.png");
            if (pathData != DBNull.Value && pathData != null && !string.IsNullOrWhiteSpace(pathData.ToString()))
            {
                string path = pathData.ToString();
                if (path.StartsWith("~") || path.StartsWith("/"))
                {
                    try { return ResolveUrl(path); } catch { return placeholder; }
                }
                else if (path.StartsWith("http"))
                {
                    return path;
                }
            }
            return placeholder;
        }

        protected string FormatDescription(string description)
        {
            if (string.IsNullOrWhiteSpace(description))
            {
                return "<p class='italic text-gray-500'>Chưa có mô tả cho sách này.</p>";
            }
            return HttpUtility.HtmlEncode(description).Replace("\n", "<br />");
        }

        protected string GetAvatarUrl(object anhNenData)
        {
            string defaultAvatar = ResolveUrl("~/Images/default_avatar.png");
            if (anhNenData is byte[] bytes && bytes.Length > 0)
            {
                return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            }
            return defaultAvatar;
        }

        protected string FormatCommentText(object binhLuanData)
        {
            return binhLuanData != null ? HttpUtility.HtmlEncode(binhLuanData.ToString()) : "";
        }

        protected string FormatRelativeTime(object ngayBinhLuanObj)
        {
            if (ngayBinhLuanObj is DateTime ngayBinhLuan)
            {
                TimeSpan diff = DateTime.Now.Subtract(ngayBinhLuan);
                if (diff.TotalSeconds < 60) return "vài giây trước";
                if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} phút trước";
                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} giờ trước";
                if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} ngày trước";
                return ngayBinhLuan.ToString("dd/MM/yyyy");
            }
            return "không xác định";
        }

        private void ShowMessage(string message, bool isError)
        {
            if (lblMessage == null) return;
            string cssClass = "mb-6 p-4 rounded-md text-sm font-medium border ";
            cssClass += isError ? "bg-red-50 border-red-300 text-red-700" : "bg-green-50 border-green-300 text-green-700";
            lblMessage.Text = HttpUtility.HtmlEncode(message);
            lblMessage.CssClass = cssClass;
            lblMessage.Visible = true;
        }

        private void LogErrorAndShowMessage(string detailedLogMessage, string userMessage)
        {
            System.Diagnostics.Trace.TraceError(detailedLogMessage);
            ShowMessage(userMessage, true);
        }

        private void DisableContentPanels()
        {
            if (pnlBookDetails != null) pnlBookDetails.Visible = false;
        }

        private void UpdateMasterCartCount()
        {
            try
            {
                // *** ĐÂY LÀ DÒNG CODE ĐÃ SỬA ***
                var master = Master as Webebook.WebForm.User.UserMaster;
                master?.UpdateCartCount();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"Error accessing Master Page for cart count update: {ex.Message}");
            }
        }
        #endregion
    }
}