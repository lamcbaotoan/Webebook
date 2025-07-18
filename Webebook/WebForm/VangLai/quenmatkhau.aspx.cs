using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Web.UI;
using System.Web.UI.WebControls;

namespace Webebook.WebForm.VangLai
{
    public partial class quenmatkhau : System.Web.UI.Page
    {
        string connectionString = ConfigurationManager.ConnectionStrings["datawebebookConnectionString"].ConnectionString;

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                // Giữ nguyên logic kiểm tra session
                if (Session["EmailReset"] != null && Session["CodeExpiry"] != null && (DateTime)Session["CodeExpiry"] > DateTime.Now)
                {
                    txtEmailOrUsername.Text = Session["EmailToDisplay"]?.ToString() ?? Session["EmailReset"].ToString();
                    lblDisplayEmail.Text = Session["EmailToDisplay"]?.ToString() ?? Session["EmailReset"].ToString();
                }
                else
                {
                    ClearResetSession();
                }
            }
        }

        protected void btnGui_Click(object sender, EventArgs e)
        {
            Page.Validate("RequestGroup");
            if (!Page.IsValid) return;

            string emailOrUsername = txtEmailOrUsername.Text.Trim();

            // Logic kiểm tra thời gian chờ giữ nguyên
            if (Session["LastRequestTime"] != null && (DateTime.Now - (DateTime)Session["LastRequestTime"]).TotalSeconds < 60)
            {
                ShowMessage(lblMessage, "Vui lòng đợi 60 giây trước khi yêu cầu mã mới.", MessageType.Warning);
                return;
            }

            string userEmail = string.Empty;
            string maskedEmail = string.Empty;

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                try
                {
                    con.Open();
                    // === THAY ĐỔI QUERY: Tìm bằng cả Username hoặc Email ===
                    string query = "SELECT Email FROM NguoiDung WHERE Email = @Identifier OR Username = @Identifier";
                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@Identifier", emailOrUsername);
                        object result = cmd.ExecuteScalar();

                        if (result != null && result != DBNull.Value)
                        {
                            userEmail = result.ToString();
                            // === LOGIC MỚI: Tạo email đã được che giấu ===
                            maskedEmail = MaskEmail(userEmail);

                            // Tiến hành gửi email
                            string maXacNhan = GenerateConfirmationCode();
                            SendConfirmationEmail(userEmail, maXacNhan);

                            // Lưu thông tin vào Session
                            Session["MaXacNhan"] = maXacNhan;
                            Session["EmailReset"] = userEmail; // Lưu email thật để reset
                            Session["EmailToDisplay"] = maskedEmail; // Lưu email đã che để hiển thị
                            Session["CodeExpiry"] = DateTime.Now.AddMinutes(10);
                            Session["LastRequestTime"] = DateTime.Now;

                            // Chuyển sang giao diện nhập mã
                            divRequestEmail.Visible = false;
                            divResetPassword.Visible = true;
                            lblDisplayEmail.Text = maskedEmail; // Hiển thị email đã che
                            ScriptManager.RegisterStartupScript(this, this.GetType(), "StartCountdown", "startCountdown(60);", true);
                        }
                        else
                        {
                            ShowMessage(lblMessage, "Email hoặc tên đăng nhập không tồn tại trong hệ thống.", MessageType.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogError($"Lỗi btnGui_Click: {ex.ToString()}");
                    ShowMessage(lblMessage, "Đã xảy ra lỗi hệ thống. Vui lòng thử lại.", MessageType.Error);
                }
            }
        }

        protected void btnResendCode_Click(object sender, EventArgs e)
        {
            if (Session["EmailReset"] == null)
            {
                ShowMessage(lblResetMessage, "Phiên làm việc đã hết hạn, vui lòng quay lại.", MessageType.Error);
                return;
            }
            if (Session["LastRequestTime"] != null)
            {
                var timeSinceLastRequest = DateTime.Now - (DateTime)Session["LastRequestTime"];
                if (timeSinceLastRequest.TotalSeconds < 60)
                {
                    int remainingSeconds = 60 - (int)timeSinceLastRequest.TotalSeconds;
                    ShowMessage(lblResetMessage, $"Vui lòng đợi thêm {remainingSeconds} giây nữa.", MessageType.Warning);
                    ScriptManager.RegisterStartupScript(this, this.GetType(), "StartResendCountdown", $"startResendCountdown({remainingSeconds});", true);
                    return; // Dừng lại, không gửi email
                }
            }

            string email = Session["EmailReset"].ToString();
            try
            {
                string maXacNhan = GenerateConfirmationCode();
                Session["MaXacNhan"] = maXacNhan;
                Session["CodeExpiry"] = DateTime.Now.AddMinutes(10);
                Session["LastRequestTime"] = DateTime.Now;

                SendConfirmationEmail(email, maXacNhan);
                ShowMessage(lblResetMessage, "Một mã xác nhận mới đã được gửi tới email của bạn.", MessageType.Success);
                ScriptManager.RegisterStartupScript(this, this.GetType(), "StartNewResendCountdown", "startResendCountdown(60);", true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Lỗi btnResendCode_Click: {ex.ToString()}");
                ShowMessage(lblResetMessage, "Không thể gửi lại mã. Vui lòng thử lại sau.", MessageType.Error);
            }
        }

        protected void btnXacNhan_Click(object sender, EventArgs e)
        {
            Page.Validate("ResetGroup");
            if (!Page.IsValid) return;

            if (Session["CodeExpiry"] == null || DateTime.Now > (DateTime)Session["CodeExpiry"])
            {
                ShowMessage(lblResetMessage, "Mã xác nhận đã hết hạn hoặc phiên làm việc không hợp lệ. Vui lòng yêu cầu lại.", MessageType.Error);
                ClearResetSession();
                divResetPassword.Visible = false;
                divRequestEmail.Visible = true;
                return;
            }
            if (txtMaXacNhan.Text.Trim() != Session["MaXacNhan"].ToString())
            {
                ShowMessage(lblResetMessage, "Mã xác nhận không đúng.", MessageType.Error);
                return;
            }

            string email = Session["EmailReset"].ToString();
            string newPassword = txtMatKhauMoi.Text;

            using (SqlConnection con = new SqlConnection(connectionString))
            {
                try
                {
                    con.Open();
                    string query = "UPDATE NguoiDung SET MatKhau = @MatKhau WHERE Email = @Email";
                    using (SqlCommand cmd = new SqlCommand(query, con))
                    {
                        cmd.Parameters.AddWithValue("@MatKhau", newPassword);
                        cmd.Parameters.AddWithValue("@Email", email);
                        int rowsAffected = cmd.ExecuteNonQuery();

                        if (rowsAffected > 0)
                        {
                            ClearResetSession();
                            Response.Redirect("dangnhap.aspx?reset=success", false);
                        }
                        else
                        {
                            ShowMessage(lblResetMessage, "Không thể cập nhật mật khẩu. Vui lòng thử lại.", MessageType.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Lỗi btnXacNhan_Click: {ex.ToString()}");
                    ShowMessage(lblResetMessage, "Đã xảy ra lỗi khi cập nhật mật khẩu.", MessageType.Error);
                }
            }
        }

        protected void btnHuy_Click(object sender, EventArgs e)
        {
            divResetPassword.Visible = false;
            divRequestEmail.Visible = true;
            lblMessage.Visible = false;
            lblResetMessage.Visible = false;
            ClearResetSession();
        }

        #region Helper Methods

        private string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email) || !email.Contains("@"))
                return email;

            var parts = email.Split('@');
            var localPart = parts[0];
            var domainPart = parts[1];

            // Che phần tên (local part)
            string maskedLocalPart;
            if (localPart.Length <= 3)
            {
                maskedLocalPart = new string('*', localPart.Length);
            }
            else
            {
                maskedLocalPart = localPart.Substring(0, 3) + new string('*', localPart.Length - 3);
            }

            // Che phần tên miền
            var domainParts = domainPart.Split('.');
            var mainDomain = domainParts[0];
            var tld = domainParts.Length > 1 ? "." + string.Join(".", domainParts.Skip(1)) : "";

            string maskedMainDomain = new string('*', mainDomain.Length);

            return $"{maskedLocalPart}@{maskedMainDomain}{tld}";
        }
        /*
        private void SendConfirmationEmail(string toEmail, string maXacNhan)
        {
            string subject = "Webebook - Mã xác nhận đặt lại mật khẩu";
            string body = $"<p>Mã xác nhận của bạn là: <b style='font-size: 18px; color: #4F46E5;'>{maXacNhan}</b></p><p>Mã này sẽ hết hạn trong 10 phút.</p>";
            MailMessage message = new MailMessage();
            message.To.Add(toEmail);
            message.Subject = subject;
            message.Body = body;
            message.IsBodyHtml = true;
            message.From = new MailAddress(ConfigurationManager.AppSettings["SmtpUser"] ?? "webebookrecreate@gmail.com", "Webebook");
            SmtpClient smtp = new SmtpClient();
            smtp.Send(message);
        }
        */
        private void SendConfirmationEmail(string toEmail, string maXacNhan)
        {
            try
            {
                string subject = "Webebook - Mã xác nhận đặt lại mật khẩu";
                string body = $@"
            <div style='font-family: Arial, sans-serif; line-height: 1.6;'>
                <h2>Yêu cầu đặt lại mật khẩu</h2>
                <p>Chúng tôi đã nhận được yêu cầu đặt lại mật khẩu cho tài khoản Webebook của bạn.</p>
                <p>Mã xác nhận của bạn là:</p>
                <p style='font-size: 24px; font-weight: bold; color: #4F46E5; letter-spacing: 2px; border: 1px solid #ddd; padding: 10px; display: inline-block;'>{maXacNhan}</p>
                <p>Mã này sẽ hết hạn trong 10 phút. Nếu bạn không yêu cầu thay đổi này, vui lòng bỏ qua email này.</p>
                <hr style='border:none; border-top:1px solid #eee;' />
                <p>Trân trọng,<br>Đội ngũ Webebook</p>
            </div>";

                // Lấy thông tin tài khoản gửi email chung từ Web.config
                string fromEmail = ConfigurationManager.AppSettings["Smtp:General:FromAddress"];
                string fromPassword = ConfigurationManager.AppSettings["Smtp:General:Password"];
                string smtpHost = ConfigurationManager.AppSettings["Smtp:Host"];

                if (!int.TryParse(ConfigurationManager.AppSettings["Smtp:Port"], out int smtpPort)) { smtpPort = 587; }
                if (!bool.TryParse(ConfigurationManager.AppSettings["Smtp:EnableSsl"], out bool enableSsl)) { enableSsl = true; }

                if (string.IsNullOrEmpty(fromEmail) || string.IsNullOrEmpty(fromPassword) || string.IsNullOrEmpty(smtpHost))
                {
                    throw new ConfigurationErrorsException("Thông tin SMTP chung trong Web.config bị thiếu hoặc không hợp lệ.");
                }

                var message = new MailMessage();
                message.From = new MailAddress(fromEmail, "Webebook");
                message.To.Add(new MailAddress(toEmail));
                message.Subject = subject;
                message.Body = body;
                message.IsBodyHtml = true;

                var smtpClient = new SmtpClient(smtpHost)
                {
                    Port = smtpPort,
                    Credentials = new System.Net.NetworkCredential(fromEmail, fromPassword),
                    EnableSsl = enableSsl,
                };

                smtpClient.Send(message);
            }
            catch (Exception ex)
            {
                LogError($"Lỗi gửi email quên mật khẩu: {ex.ToString()}");
                throw new Exception("Không thể gửi email xác nhận. Vui lòng kiểm tra lại cấu hình và thông tin tài khoản email.");
            }
        }

        private void LogError(string v)
        {
            throw new NotImplementedException();
        }

        private string GenerateConfirmationCode()
        {
            return new Random().Next(1000, 10000).ToString("D4");
        }

        private enum MessageType { Success, Error, Warning, Info }

        private void ShowMessage(Label labelControl, string message, MessageType type)
        {
            labelControl.Text = $"<div class='flex items-center'><i class='fas fa-info-circle mr-2'></i><span>{message}</span></div>";
            string baseClasses = "block w-full p-4 mb-4 text-sm rounded-lg border";
            string specificClasses = "";
            switch (type)
            {
                case MessageType.Success: specificClasses = "bg-green-50 border-green-300 text-green-800"; break;
                case MessageType.Error: specificClasses = "bg-red-50 border-red-300 text-red-800"; break;
                case MessageType.Warning: specificClasses = "bg-yellow-50 border-yellow-300 text-yellow-800"; break;
                case MessageType.Info: specificClasses = "bg-blue-50 border-blue-300 text-blue-800"; break;
            }
            labelControl.CssClass = $"{baseClasses} {specificClasses}";
            labelControl.Visible = true;
        }

        private void ClearResetSession()
        {
            Session.Remove("MaXacNhan");
            Session.Remove("EmailReset");
            Session.Remove("LastRequestTime");
            Session.Remove("CodeExpiry");
            Session.Remove("EmailToDisplay"); // Thêm dòng này
        }
        #endregion
    }
}