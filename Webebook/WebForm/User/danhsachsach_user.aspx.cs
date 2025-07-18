using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using System.Diagnostics;

namespace Webebook.WebForm.User
{
    public enum CartAddResult { Success, AlreadyExists, Error }

    public partial class danhsachsach_user : System.Web.UI.Page
    {
        string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;
        int userId = 0;
        private const int PageSize = 12; // Tăng lên 12 để vừa với 6 cột trên màn hình lớn

        // ViewState properties
        private string CurrentSearchTerm { get { return ViewState["CurrentSearchTerm"] as string ?? string.Empty; } set { ViewState["CurrentSearchTerm"] = value; } }
        private string CurrentGenre { get { return ViewState["CurrentGenre"] as string ?? string.Empty; } set { ViewState["CurrentGenre"] = value; } }
        private int CurrentPageIndex { get { return (int)(ViewState["CurrentPageIndex"] ?? 1); } set { ViewState["CurrentPageIndex"] = value; } }
        private int TotalRows { get { return (int)(ViewState["TotalRows"] ?? 0); } set { ViewState["TotalRows"] = value; } }

        protected void Page_Load(object sender, EventArgs e)
        {
            // 1. Kiểm tra đăng nhập (giữ nguyên)
            if (Session["UserID"] == null || !int.TryParse(Session["UserID"].ToString(), out userId) || userId <= 0)
            {
                Response.Redirect("~/WebForm/VangLai/dangnhap.aspx?message=notloggedin&returnUrl=" + HttpUtility.UrlEncode(Request.Url.PathAndQuery), true);
                return;
            }

            if (!IsPostBack)
            {
                // **CẢI TIẾN**: Logic khởi tạo trang lần đầu được làm rõ ràng hơn
                LoadGenres(); // Tải danh sách thể loại trước

                // Đặt các giá trị mặc định cho bộ lọc
                CurrentSearchTerm = string.Empty;
                CurrentGenre = string.Empty; // Mặc định là không có thể loại nào được chọn
                CurrentPageIndex = 1;

                // Cập nhật giao diện người dùng để phản ánh trạng thái mặc định
                txtSearchFilter.Text = CurrentSearchTerm;
                ddlGenreFilter.SelectedValue = CurrentGenre; // Chọn mục "-- Tất cả thể loại --"

                // Tải danh sách sách lần đầu
                LoadBookList();
            }

            if (!IsPostBack && string.IsNullOrEmpty(lblMessage.Text))
            {
                lblMessage.Visible = false;
            }
        }

        // **CẢI TIẾN**: Thêm một vòng lặp để gán DataSource thay vì DataBind trực tiếp
        private void LoadGenres()
        {
            DataTable dtGenres = GetDistinctGenres();
            ddlGenreFilter.Items.Clear();
            // Đảm bảo dòng "Tất cả" luôn ở đầu
            ddlGenreFilter.Items.Insert(0, new ListItem("-- Tất cả thể loại --", string.Empty));

            if (dtGenres != null && dtGenres.Rows.Count > 0)
            {
                foreach (DataRow row in dtGenres.Rows)
                {
                    string genre = row["Value"].ToString();
                    ddlGenreFilter.Items.Add(new ListItem(genre, genre));
                }
            }
        }

        private DataTable GetDistinctGenres()
        {
            DataTable dt = new DataTable();
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                string query = @"
                    SELECT DISTINCT LTRIM(RTRIM(Value)) AS Value FROM (
                        SELECT DISTINCT LoaiSach as Value FROM Sach WHERE LoaiSach IS NOT NULL AND LoaiSach <> ''
                        UNION
                        SELECT value
                        FROM Sach
                        CROSS APPLY STRING_SPLIT(ISNULL(TheLoaiChuoi,''), ',')
                        WHERE NULLIF(LTRIM(RTRIM(value)), '') IS NOT NULL
                    ) AS Genres
                    WHERE Value <> ''
                    ORDER BY Value;";
                using (SqlCommand cmd = new SqlCommand(query, con))
                {
                    try { con.Open(); SqlDataAdapter da = new SqlDataAdapter(cmd); da.Fill(dt); }
                    catch (Exception ex) { LogError("Error Loading Genres: " + ex.ToString()); ShowMessage("Lỗi tải danh sách thể loại.", true); return null; }
                }
            }
            return dt;
        }

        // Logic LoadBookList đã rất tốt, giữ nguyên và chỉ dọn dẹp format
        private void LoadBookList()
        {
            DataTable dt = new DataTable();
            StringBuilder queryBuilder = new StringBuilder(@"
                WITH FilteredSach AS (
                    SELECT 
                        IDSach, TenSach, TacGia, GiaSach, DuongDanBiaSach,
                        ROW_NUMBER() OVER (ORDER BY TenSach) AS RowNum 
                    FROM Sach 
                    WHERE 1 = 1 ");
            List<SqlParameter> parameters = new List<SqlParameter>();

            if (!string.IsNullOrEmpty(CurrentSearchTerm)) { queryBuilder.Append("AND (TenSach LIKE @SearchTerm OR TacGia LIKE @SearchTerm) "); parameters.Add(new SqlParameter("@SearchTerm", $"%{CurrentSearchTerm}%")); }
            if (!string.IsNullOrEmpty(CurrentGenre)) { queryBuilder.Append(@"AND (LoaiSach = @Genre OR CHARINDEX(',' + @TrimmedGenre + ',', ',' + LTRIM(RTRIM(ISNULL(TheLoaiChuoi, ''))) + ',') > 0) "); parameters.Add(new SqlParameter("@Genre", CurrentGenre)); parameters.Add(new SqlParameter("@TrimmedGenre", CurrentGenre.Trim())); }

            queryBuilder.Append(@") 
                SELECT IDSach, TenSach, TacGia, GiaSach, DuongDanBiaSach 
                FROM FilteredSach 
                WHERE RowNum > @StartRowIndex AND RowNum <= @EndRowIndex; ");

            StringBuilder countQueryBuilder = new StringBuilder("SELECT COUNT(*) FROM Sach WHERE 1 = 1 ");
            if (!string.IsNullOrEmpty(CurrentSearchTerm)) { countQueryBuilder.Append("AND (TenSach LIKE @SearchTerm OR TacGia LIKE @SearchTerm) "); }
            if (!string.IsNullOrEmpty(CurrentGenre)) { countQueryBuilder.Append(@"AND (LoaiSach = @Genre OR CHARINDEX(',' + @TrimmedGenre + ',', ',' + LTRIM(RTRIM(ISNULL(TheLoaiChuoi, ''))) + ',') > 0) "); }

            int startRowIndex = (CurrentPageIndex - 1) * PageSize;
            int endRowIndex = CurrentPageIndex * PageSize;
            parameters.Add(new SqlParameter("@StartRowIndex", startRowIndex));
            parameters.Add(new SqlParameter("@EndRowIndex", endRowIndex));

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                using (SqlCommand cmd = new SqlCommand(queryBuilder.ToString(), con))
                {
                    cmd.Parameters.AddRange(parameters.ToArray());
                    using (SqlCommand countCmd = new SqlCommand(countQueryBuilder.ToString(), con))
                    {
                        if (!string.IsNullOrEmpty(CurrentSearchTerm)) { countCmd.Parameters.Add(new SqlParameter("@SearchTerm", $"%{CurrentSearchTerm}%")); }
                        if (!string.IsNullOrEmpty(CurrentGenre)) { countCmd.Parameters.Add(new SqlParameter("@Genre", CurrentGenre)); countCmd.Parameters.Add(new SqlParameter("@TrimmedGenre", CurrentGenre.Trim())); }
                        try
                        {
                            con.Open();
                            object countResult = countCmd.ExecuteScalar();
                            TotalRows = (countResult != DBNull.Value) ? Convert.ToInt32(countResult) : 0;
                            SqlDataAdapter da = new SqlDataAdapter(cmd);
                            da.Fill(dt);
                            rptSachUser.DataSource = dt;
                            rptSachUser.DataBind();
                            bool hasData = dt.Rows.Count > 0;
                            pnlEmptyData.Visible = !hasData;
                            bookGridContainer.Visible = hasData;
                            if (hasData && IsPostBack)
                            {
                                ScriptManager.RegisterStartupScript(this, GetType(), "ReInitFadeInUserList", "setTimeout(initializeCardFadeInUserList, 100);", true);
                            }
                        }
                        catch (Exception ex) { LogError("LoadBookList Error: " + ex.ToString()); ShowMessage("Lỗi tải danh sách sách. Vui lòng thử lại.", true); TotalRows = 0; rptSachUser.DataSource = null; rptSachUser.DataBind(); pnlEmptyData.Visible = true; bookGridContainer.Visible = false; }
                        finally { UpdatePagerControls(); }
                    }
                }
            }
        }

        // **CẢI TIẾN**: Logic btnClearFilter_Click được làm nhất quán hơn
        protected void btnClearFilter_Click(object sender, EventArgs e)
        {
            CurrentSearchTerm = string.Empty;
            CurrentGenre = string.Empty;
            txtSearchFilter.Text = string.Empty;
            ddlGenreFilter.SelectedValue = string.Empty; // Chọn lại mục "Tất cả"
            CurrentPageIndex = 1;
            LoadBookList();
            ShowMessage("Đã xóa bộ lọc.", false);
        }

        protected void btnApplyFilter_Click(object sender, EventArgs e)
        {
            CurrentSearchTerm = txtSearchFilter.Text.Trim();
            CurrentGenre = ddlGenreFilter.SelectedValue;
            CurrentPageIndex = 1; // Luôn về trang 1 khi lọc
            LoadBookList();
        }

        // Các phương thức còn lại không cần thay đổi
        // ... (UpdatePagerControls, rptSachUser_ItemCommand, Pager_Click, AddToCart, GetBookName, ShowMessage, LogError, GetImageUrl) ...
        #region Unchanged Methods
        private void UpdatePagerControls() 
        { int totalPages = (int)Math.Ceiling((double)TotalRows / PageSize); 
            lblPagerInfo.Text = totalPages > 0 ? $"Trang {CurrentPageIndex} / {totalPages}" : "Không có sách"; 
            btnPrevPage.Enabled = (CurrentPageIndex > 1); btnNextPage.Enabled = (CurrentPageIndex < totalPages); 
            bool pagerVisible = (totalPages > 1); btnPrevPage.Visible = pagerVisible; lblPagerInfo.Visible = (totalPages > 0); 
            btnNextPage.Visible = pagerVisible; 
        }
        protected void rptSachUser_ItemCommand(object source, RepeaterCommandEventArgs e) { if (e.CommandName == "AddToCart") 
            { if (userId <= 0) { Response.Redirect("~/WebForm/VangLai/dangnhap.aspx?message=sessionexpired&returnUrl=" + HttpUtility.UrlEncode(Request.Url.PathAndQuery), true); 
                    return; 
                } try 
                { int idSach = Convert.ToInt32(e.CommandArgument); 
                    string bookName = GetBookName(idSach); CartAddResult result = AddToCart(userId, idSach); 
                    switch (result) 
                    { case CartAddResult.Success: ShowMessage($"Đã thêm '{HttpUtility.HtmlEncode(bookName)}' vào giỏ hàng.", false); 
                            if (Master is UserMaster master) { master.UpdateCartCount(); 
                            } break;
                        case CartAddResult.AlreadyExists:
                            string cartUrl = ResolveUrl("~/WebForm/User/giohang_user.aspx");
                            string encodedBookName = HttpUtility.JavaScriptStringEncode(bookName);

                            // Tạo script gọi SweetAlert2 thay vì confirm()
                            string script = $@"
                            Swal.fire({{
                                title: 'Sách đã có trong giỏ hàng',
                                html: `Sách <strong>""{encodedBookName}""</strong> đã tồn tại trong giỏ hàng của bạn.`,
                                icon: 'info',
                                showCancelButton: true,
                                confirmButtonColor: '#3B82F6', // blue-500
                                cancelButtonColor: '#6B7280',  // gray-500
                                confirmButtonText: '<i class=""fas fa-shopping-cart""></i> Xem giỏ hàng',
                                cancelButtonText: 'Tiếp tục mua sắm'
                            }}).then((result) => {{
                                if (result.isConfirmed) {{
                                    window.location.href = '{cartUrl}';
                                }}
                            }});";

                            // Phần ScriptManager giữ nguyên, chỉ thay đổi nội dung script
                            Control btnSender = e.Item.FindControl("btnAddToCart");
                            if (btnSender != null)
                            {
                                ScriptManager.RegisterStartupScript(btnSender, btnSender.GetType(), $"ConfirmCartRedirect_{idSach}", script, true);
                            }
                            else
                            {
                                ScriptManager.RegisterStartupScript(this, this.GetType(), $"ConfirmCartRedirect_{idSach}", script, true);
                            }
                            break;
                    } 
                } catch (Exception ex) { LogError("Lỗi rptSachUser_ItemCommand khi thêm vào giỏ hàng: " + ex.ToString()); 
                    ShowMessage("Đã xảy ra lỗi khi thêm vào giỏ hàng.", true); 
                } 
            } 
        }
        protected void Pager_Click(object sender, EventArgs e) 
        { Button btn = (Button)sender; string command = btn.CommandArgument; 
            int totalPages = (int)Math.Ceiling((double)TotalRows / PageSize); 
            if (command == "Prev" && CurrentPageIndex > 1) { CurrentPageIndex--; 
            } 
            else if (command == "Next" && CurrentPageIndex < totalPages) { CurrentPageIndex++; 
            } LoadBookList(); 
        }
        private CartAddResult AddToCart(int currentUserId, int idSach)
        { using (SqlConnection con = new SqlConnection(connectionString)) 
            { string checkQuery = "SELECT COUNT(*) FROM GioHang WHERE IDNguoiDung = @UserId AND IDSach = @IDSach"; 
                try { con.Open(); 
                    using (SqlCommand checkCmd = new SqlCommand(checkQuery, con)) 
                    { checkCmd.Parameters.AddWithValue("@UserId", currentUserId); 
                        checkCmd.Parameters.AddWithValue("@IDSach", idSach); 
                        if ((int)checkCmd.ExecuteScalar() > 0) return CartAddResult.AlreadyExists; 
                    } string insertQuery = "INSERT INTO GioHang (IDNguoiDung, IDSach, SoLuong) VALUES (@UserId, @IDSach, 1)"; 
                    using (SqlCommand insertCmd = new SqlCommand(insertQuery, con)) 
                    { insertCmd.Parameters.AddWithValue("@UserId", currentUserId); 
                        insertCmd.Parameters.AddWithValue("@IDSach", idSach); return insertCmd.ExecuteNonQuery() > 0 ? CartAddResult.Success : CartAddResult.Error;
                    } 
                } catch (Exception ex) { ShowMessage("Lỗi khi thêm vào giỏ hàng.", true); 
                    LogError($"Lỗi AddToCart User {currentUserId}, Sach {idSach}: {ex}"); 
                    return CartAddResult.Error; } 
            } 
        }
        private string GetBookName(int idSach) 
        { string bookName = "Sách"; 
            try { 
                using (SqlConnection con = new SqlConnection(connectionString)) 
                { string query = "SELECT TenSach FROM Sach WHERE IDSach = @IDSach"; 
                    using (SqlCommand cmd = new SqlCommand(query, con)) 
                    { cmd.Parameters.AddWithValue("@IDSach", idSach);
                        con.Open(); object result = cmd.ExecuteScalar(); 
                        if (result != null && result != DBNull.Value) bookName = result.ToString(); 
                    }
                } 
            } catch (Exception ex) { LogError("GetBookName Error: " + ex.ToString());
            } return bookName;
        }
        private void ShowMessage(string message, bool isError) 
        { lblMessage.Text = HttpUtility.HtmlEncode(message); 
            string cssClass = "block w-full p-4 mb-6 text-sm rounded-lg border "; 
            if (isError) { 
                cssClass += "bg-red-50 border-red-300 text-red-800"; 
            } 
            else { 
                cssClass += "bg-green-50 border-green-300 text-green-800"; 
            } 
            lblMessage.CssClass = cssClass; 
            lblMessage.Visible = true; 
        }
        private void LogError(string errorMessage) 
        { Debug.WriteLine(errorMessage); }
        protected string GetImageUrl(object pathData) 
        { string defaultImage = ResolveUrl("~/Images/placeholder_cover.png"); 
            if (pathData != DBNull.Value && pathData != null && !string.IsNullOrEmpty(pathData.ToString())) 
            { string path = pathData.ToString(); if (path.StartsWith("~") || path.StartsWith("/")) 
                { try { return ResolveUrl(path); 
                    } 
                    catch { return defaultImage; 
                    } 
                } 
                else if (path.StartsWith("http://") || path.StartsWith("https://"))
                { return path; 
                } 
                else { 
                    return defaultImage; 
                } 
            } 
            return defaultImage; 
        }
        #endregion
    }
}