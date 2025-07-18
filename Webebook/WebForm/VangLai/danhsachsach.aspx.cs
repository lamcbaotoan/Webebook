using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Text;
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;
using System.Web.UI.HtmlControls;
using System.Diagnostics;

namespace Webebook.WebForm.VangLai
{
    public partial class danhsachsach : System.Web.UI.Page
    {
        string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;
        private const int PageSize = 10; // Số sách mỗi trang

        // ViewState properties
        private string CurrentSearchTerm { get { return ViewState["CurrentSearchTerm"] as string ?? string.Empty; } set { ViewState["CurrentSearchTerm"] = value; } }
        private string CurrentGenre { get { return ViewState["CurrentGenre"] as string ?? string.Empty; } set { ViewState["CurrentGenre"] = value; } }
        private int CurrentPageIndex { get { return (int)(ViewState["CurrentPageIndex"] ?? 1); } set { ViewState["CurrentPageIndex"] = value; } }
        private int TotalRows { get { return (int)(ViewState["TotalRows"] ?? 0); } set { ViewState["TotalRows"] = value; } }

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                // Tải danh sách thể loại trước
                LoadGenres();

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

        private void LoadGenres()
        {
            DataTable dtGenres = GetDistinctGenres();
            ddlGenreFilter.Items.Clear();
            // **THAY ĐỔI QUAN TRỌNG**: Thêm dòng "Tất cả" làm lựa chọn mặc định.
            // Giá trị của nó là chuỗi rỗng (""), tương ứng với việc không lọc theo thể loại.
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
                // Query này đã được tối ưu để lấy tất cả các thể loại duy nhất từ cả hai cột
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
                    catch (Exception ex) { LogError("Error Loading Genres (Public): " + ex.ToString()); ShowMessage("Lỗi tải danh sách thể loại.", true); return null; }
                }
            }
            return dt;
        }

        private void LoadBookList()
        {
            DataTable dt = new DataTable();
            // Sử dụng CTE và ROW_NUMBER() để phân trang hiệu quả trên server
            StringBuilder queryBuilder = new StringBuilder(@"
                WITH FilteredSach AS (
                    SELECT 
                        IDSach, TenSach, TacGia, GiaSach, DuongDanBiaSach,
                        ROW_NUMBER() OVER (ORDER BY TenSach) AS RowNum 
                    FROM Sach 
                    WHERE 1 = 1 ");
            List<SqlParameter> parameters = new List<SqlParameter>();

            // Thêm điều kiện lọc tìm kiếm
            if (!string.IsNullOrEmpty(CurrentSearchTerm))
            {
                queryBuilder.Append("AND (TenSach LIKE @SearchTerm OR TacGia LIKE @SearchTerm) ");
                parameters.Add(new SqlParameter("@SearchTerm", $"%{CurrentSearchTerm}%"));
            }

            // Thêm điều kiện lọc thể loại
            if (!string.IsNullOrEmpty(CurrentGenre))
            {
                // Tìm kiếm chính xác trong cột LoaiSach hoặc trong chuỗi TheLoaiChuoi
                queryBuilder.Append(@"AND (LoaiSach = @Genre OR CHARINDEX(',' + @TrimmedGenre + ',', ',' + LTRIM(RTRIM(ISNULL(TheLoaiChuoi, ''))) + ',') > 0) ");
                parameters.Add(new SqlParameter("@Genre", CurrentGenre));
                parameters.Add(new SqlParameter("@TrimmedGenre", CurrentGenre.Trim()));
            }

            queryBuilder.Append(@") 
                SELECT IDSach, TenSach, TacGia, GiaSach, DuongDanBiaSach 
                FROM FilteredSach 
                WHERE RowNum > @StartRowIndex AND RowNum <= @EndRowIndex; ");

            // Query để đếm tổng số dòng phù hợp với bộ lọc
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
                        // Thêm parameters cho count query
                        if (!string.IsNullOrEmpty(CurrentSearchTerm)) { countCmd.Parameters.Add(new SqlParameter("@SearchTerm", $"%{CurrentSearchTerm}%")); }
                        if (!string.IsNullOrEmpty(CurrentGenre)) { countCmd.Parameters.Add(new SqlParameter("@Genre", CurrentGenre)); countCmd.Parameters.Add(new SqlParameter("@TrimmedGenre", CurrentGenre.Trim())); }

                        try
                        {
                            con.Open();
                            // Lấy tổng số dòng trước
                            object countResult = countCmd.ExecuteScalar();
                            TotalRows = (countResult != DBNull.Value) ? Convert.ToInt32(countResult) : 0;

                            // Lấy dữ liệu cho trang hiện tại
                            SqlDataAdapter da = new SqlDataAdapter(cmd); da.Fill(dt);
                            rptSach.DataSource = dt; rptSach.DataBind();

                            bool hasData = dt.Rows.Count > 0;
                            pnlEmptyData.Visible = !hasData;
                            bookGridContainer.Visible = hasData;

                            if (hasData && IsPostBack)
                            {
                                ScriptManager.RegisterStartupScript(this, GetType(), "ReInitFadeIn", "setTimeout(initializeCardFadeIn, 100);", true);
                            }
                        }
                        catch (Exception ex) { LogError("LoadBookList Error (Public): " + ex.ToString()); ShowMessage("Lỗi tải danh sách sách.", true); TotalRows = 0; rptSach.DataSource = null; rptSach.DataBind(); pnlEmptyData.Visible = true; bookGridContainer.Visible = false; }
                        finally { UpdatePagerControls(); }
                    }
                }
            }
        }

        // Các phương thức còn lại không thay đổi đáng kể so với code gốc của bạn
        // ... (UpdatePagerControls, btnApplyFilter_Click, btnClearFilter_Click, Pager_Click, ShowMessage, LogError, GetImageUrl)
        private void UpdatePagerControls()
        {
            int totalPages = (int)Math.Ceiling((double)TotalRows / PageSize);
            lblPagerInfo.Text = totalPages > 0 ? $"Trang {CurrentPageIndex} / {totalPages}" : "Không có sách";
            btnPrevPage.Enabled = (CurrentPageIndex > 1);
            btnNextPage.Enabled = (CurrentPageIndex < totalPages);
            bool pagerVisible = (totalPages > 1);
            btnPrevPage.Visible = pagerVisible;
            lblPagerInfo.Visible = (totalPages > 0);
            btnNextPage.Visible = pagerVisible;
        }

        protected void btnApplyFilter_Click(object sender, EventArgs e)
        {
            CurrentSearchTerm = txtSearchFilter.Text.Trim();
            CurrentGenre = ddlGenreFilter.SelectedValue;
            CurrentPageIndex = 1; // Luôn quay về trang đầu khi áp dụng bộ lọc mới
            LoadBookList();
        }

        protected void btnClearFilter_Click(object sender, EventArgs e)
        {
            CurrentSearchTerm = string.Empty;
            CurrentGenre = string.Empty;
            txtSearchFilter.Text = string.Empty;
            ddlGenreFilter.SelectedValue = string.Empty; // Chọn lại mục "Tất cả thể loại"
            CurrentPageIndex = 1;
            LoadBookList();
            ShowMessage("Đã xóa bộ lọc.", false);
        }

        protected void Pager_Click(object sender, EventArgs e)
        {
            Button btn = (Button)sender;
            string command = btn.CommandArgument;
            int totalPages = (int)Math.Ceiling((double)TotalRows / PageSize);
            if (command == "Prev" && CurrentPageIndex > 1) { CurrentPageIndex--; }
            else if (command == "Next" && CurrentPageIndex < totalPages) { CurrentPageIndex++; }
            LoadBookList();
        }

        private void ShowMessage(string message, bool isError)
        {
            lblMessage.Text = HttpUtility.HtmlEncode(message);
            string cssClass = "block w-full p-4 mb-6 text-sm rounded-lg border ";
            if (isError) { cssClass += "bg-red-50 border-red-300 text-red-800"; }
            else { cssClass += "bg-green-50 border-green-300 text-green-800"; }
            lblMessage.CssClass = cssClass;
            lblMessage.Visible = true;
        }

        private void LogError(string errorMessage) { Debug.WriteLine(errorMessage); }

        protected string GetImageUrl(object pathData)
        {
            string defaultImage = ResolveUrl("~/Images/placeholder_cover.png");
            if (pathData != DBNull.Value && pathData != null && !string.IsNullOrEmpty(pathData.ToString()))
            {
                string path = pathData.ToString();
                if (path.StartsWith("~") || path.StartsWith("/"))
                {
                    try { return ResolveUrl(path); }
                    catch { return defaultImage; }
                }
                else if (path.StartsWith("http://") || path.StartsWith("https://"))
                {
                    return path;
                }
                else { return defaultImage; }
            }
            return defaultImage;
        }
    }
}