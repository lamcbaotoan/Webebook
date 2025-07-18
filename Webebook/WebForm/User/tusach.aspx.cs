using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Text; // Thêm using cho StringBuilder
using System.Web;
using System.Web.UI;
using System.Web.UI.WebControls;


namespace Webebook.WebForm.User
{
    public partial class tusach : System.Web.UI.Page
    {
        string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;
        int userId = 0;

        // **MỚI**: Dùng ViewState để lưu lại từ khóa tìm kiếm giữa các lần postback
        private string CurrentSearchTerm
        {
            get { return ViewState["BookshelfSearchTerm"] as string ?? string.Empty; }
            set { ViewState["BookshelfSearchTerm"] = value; }
        }

        // File: tusach.aspx.cs

        protected void Page_Load(object sender, EventArgs e)
        {
            // Kiểm tra đăng nhập (giữ nguyên)
            if (Session["UserID"] == null || !int.TryParse(Session["UserID"].ToString(), out userId) || userId <= 0)
            {
                Response.Redirect(ResolveUrl("~/WebForm/VangLai/dangnhap.aspx") + "?returnUrl=" + Server.UrlEncode(Request.Url.PathAndQuery));
                return;
            }

            if (!IsPostBack)
            {
                // *** BẮT ĐẦU CODE MỚI ***
                // Kiểm tra nếu có thông báo lỗi được truyền qua URL
                if (Request.QueryString["error"] == "notowned")
                {
                    ShowMessage("Bạn chưa sở hữu sách này hoặc không có quyền truy cập.", true);
                }
                // *** KẾT THÚC CODE MỚI ***

                LoadBookshelf();
                UpdateMasterCartCount();
            }

            // Tạm thời ẩn message trên các postback khác để tránh hiển thị lại lỗi
            if (IsPostBack)
            {
                lblMessage.Visible = false;
            }
        }

        // **MỚI**: Sự kiện cho nút Tìm Kiếm
        protected void btnSearchBookshelf_Click(object sender, EventArgs e)
        {
            CurrentSearchTerm = txtSearchBookshelf.Text.Trim();
            LoadBookshelf();
        }

        // **MỚI**: Sự kiện cho nút Xóa Lọc
        protected void btnClearSearch_Click(object sender, EventArgs e)
        {
            CurrentSearchTerm = string.Empty;
            txtSearchBookshelf.Text = string.Empty;
            LoadBookshelf();
        }

        // **NÂNG CẤP**: Sửa đổi phương thức LoadBookshelf để thêm điều kiện tìm kiếm
        private void LoadBookshelf()
        {
            using (SqlConnection con = new SqlConnection(connectionString))
            {
                // Dùng StringBuilder để xây dựng câu truy vấn động
                var queryBuilder = new StringBuilder(@"
                    SELECT
                        ts.IDSach, s.TenSach, ISNULL(s.TacGia, 'N/A') AS TacGia,
                        s.DuongDanBiaSach, ts.ViTriDoc,
                        ISNULL(ChapCount.TotalChapters, 0) AS TotalChapters
                    FROM TuSach ts
                    JOIN Sach s ON ts.IDSach = s.IDSach
                    LEFT JOIN (
                        SELECT IDSach, COUNT(DISTINCT SoChuong) AS TotalChapters
                        FROM NoiDungSach
                        GROUP BY IDSach
                    ) AS ChapCount ON ts.IDSach = ChapCount.IDSach
                    WHERE ts.IDNguoiDung = @UserId
                ");

                // Thêm điều kiện tìm kiếm nếu có từ khóa
                if (!string.IsNullOrWhiteSpace(CurrentSearchTerm))
                {
                    queryBuilder.Append(" AND (s.TenSach LIKE @SearchTerm OR s.TacGia LIKE @SearchTerm) ");
                }

                queryBuilder.Append(" ORDER BY ts.NgayThem DESC");

                using (SqlCommand cmd = new SqlCommand(queryBuilder.ToString(), con))
                {
                    cmd.Parameters.AddWithValue("@UserId", userId);

                    // Thêm tham số tìm kiếm nếu có
                    if (!string.IsNullOrWhiteSpace(CurrentSearchTerm))
                    {
                        cmd.Parameters.AddWithValue("@SearchTerm", $"%{CurrentSearchTerm}%");
                    }

                    try
                    {
                        con.Open();
                        SqlDataAdapter da = new SqlDataAdapter(cmd);
                        DataTable dt = new DataTable();
                        da.Fill(dt);

                        if (dt.Rows.Count > 0)
                        {
                            rptTuSach.DataSource = dt;
                            rptTuSach.DataBind();
                            pnlBookshelfGrid.Visible = true;
                            pnlEmptyBookshelf.Visible = false;
                        }
                        else
                        {
                            rptTuSach.DataSource = null;
                            rptTuSach.DataBind();
                            pnlBookshelfGrid.Visible = false;
                            pnlEmptyBookshelf.Visible = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowMessage("Lỗi khi tải tủ sách: " + ex.Message, true);
                        pnlBookshelfGrid.Visible = false;
                        pnlEmptyBookshelf.Visible = true;
                        System.Diagnostics.Debug.WriteLine("LoadBookshelf Error: " + ex.ToString());
                    }
                }
            }
        }

        // Các phương thức còn lại giữ nguyên không thay đổi
        #region Unchanged Methods
        protected void rptTuSach_ItemDataBound(object sender, RepeaterItemEventArgs e)
        {
            if (e.Item.ItemType == ListItemType.Item || e.Item.ItemType == ListItemType.AlternatingItem)
            {
                DataRowView drv = e.Item.DataItem as DataRowView;
                if (drv != null)
                {
                    Literal litProgress = (Literal)e.Item.FindControl("litProgress");
                    HyperLink hlReadButton = (HyperLink)e.Item.FindControl("hlReadButton");
                    Literal litReadButtonText = (Literal)e.Item.FindControl("litReadButtonText");

                    if (litProgress == null || hlReadButton == null || litReadButtonText == null)
                    {
                        System.Diagnostics.Debug.WriteLine("Error finding controls in ItemTemplate for Repeater rptTuSach.");
                        return;
                    }

                    try
                    {
                        string idSach = drv["IDSach"].ToString();
                        object viTriDocObj = drv["ViTriDoc"];
                        int totalChapters = Convert.ToInt32(drv["TotalChapters"]);
                        string viTriDocStr = (viTriDocObj == DBNull.Value || viTriDocObj == null || string.IsNullOrWhiteSpace(viTriDocObj.ToString()) || viTriDocObj.ToString() == "0") ? null : viTriDocObj.ToString();

                        string progressText = "";
                        string buttonText = "Bắt đầu đọc";
                        string readUrl = ResolveUrl($"~/WebForm/User/docsach.aspx?IDSach={idSach}");
                        bool isButtonEnabled = true;

                        if (totalChapters == 0)
                        {
                            progressText = "Chưa có nội dung";
                            buttonText = "Chưa có nội dung";
                            isButtonEnabled = false;
                        }
                        else if (string.IsNullOrEmpty(viTriDocStr))
                        {
                            progressText = $"Chưa đọc / {totalChapters} chương";
                        }
                        else
                        {
                            progressText = $"Đã đọc đến chương {viTriDocStr} / {totalChapters}";
                            buttonText = $"Đọc tiếp (Chương {viTriDocStr})";
                            readUrl = ResolveUrl($"~/WebForm/User/docsach.aspx?IDSach={idSach}&SoChuong={viTriDocStr}");
                        }

                        litProgress.Text = progressText;
                        litReadButtonText.Text = buttonText;

                        if (isButtonEnabled)
                        {
                            hlReadButton.NavigateUrl = readUrl;
                            hlReadButton.Enabled = true;
                            if (hlReadButton.CssClass.Contains(" disabled"))
                                hlReadButton.CssClass = hlReadButton.CssClass.Replace(" disabled", "");
                        }
                        else
                        {
                            hlReadButton.NavigateUrl = "#";
                            hlReadButton.Enabled = false;
                            if (!hlReadButton.CssClass.Contains(" disabled"))
                                hlReadButton.CssClass += " disabled";
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error in rptTuSach_ItemDataBound for IDSach {drv["IDSach"]}: {ex.Message}");
                        litProgress.Text = "Lỗi hiển thị";
                        hlReadButton.Enabled = false;
                        hlReadButton.CssClass = "read-button disabled";
                        litReadButtonText.Text = "Lỗi";
                    }
                }
            }
        }
        private void ShowMessage(string message, bool isError)
        {
            lblMessage.Text = Server.HtmlEncode(message);
            lblMessage.CssClass = isError ? "block mb-4 p-3 rounded-md border bg-red-50 border-red-300 text-red-700 text-sm" : "block mb-4 p-3 rounded-md border bg-green-50 border-green-300 text-green-700 text-sm";
            lblMessage.Visible = true;
        }
        private void UpdateMasterCartCount()
        {
            try
            {
                Webebook.WebForm.User.UserMaster master = Master as Webebook.WebForm.User.UserMaster;
                if (master != null)
                {
                    master.UpdateCartCount();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error accessing Master Page: {ex.Message}");
            }
        }
        #endregion
    }
}