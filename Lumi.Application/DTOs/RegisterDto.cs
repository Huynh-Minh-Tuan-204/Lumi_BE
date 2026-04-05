namespace Lumi.Application.DTOs
{
    public class RegisterDto
    {
        public string EmployeeCode { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string FullName { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }      // Để nhận số điện thoại từ React
        public int RoleId { get; set; }
    }
}