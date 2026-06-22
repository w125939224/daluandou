using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace daluandou.Models
{
    public class User
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required(ErrorMessage = "用户名不能为空")]
        [StringLength(50)]
        public string Username { get; set; } = string.Empty;

        [Required(ErrorMessage = "密码不能为空")]
        [StringLength(20, MinimumLength = 6, ErrorMessage = "密码长度必须在6-20个字符之间")]
        public string Password { get; set; } = string.Empty;

        [StringLength(100)]
        public string PasswordHash { get; set; } = string.Empty;

        [Required(ErrorMessage = "邮箱不能为空")]
        [EmailAddress(ErrorMessage = "邮箱格式不正确")]
        public string Email { get; set; } = string.Empty;

        [Required(ErrorMessage = "手机号不能为空")]
        [StringLength(11, MinimumLength = 11, ErrorMessage = "手机号必须是11位数字")]
        [RegularExpression(@"^1[3-9]\d{9}$", ErrorMessage = "手机号格式不正确")]
        public string PhoneNumber { get; set; } = string.Empty;
        public DateTime CreateTime { get; set; } = DateTime.Now;

        public int WinCount { get; set; }
        public int LoseCount { get; set; }
        public int TotalGames { get; set; }

    }
}