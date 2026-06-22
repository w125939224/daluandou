using daluandou.Data;
using daluandou.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using daluandou.Data;

namespace daluandou.Pages
{
    public class LoginModel : PageModel
    {
        private readonly AppDbContext _context;

        public LoginModel(AppDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public LoginInputModel Input { get; set; } = new LoginInputModel();

        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            // 已登录（Session 存在）则直接跳转 Profile
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId != null)
            {
                return RedirectToPage("/Profile");
            }
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                return Page();
            }

            // 查找用户
            var user = await _context.Users
                .FirstOrDefaultAsync(u => u.Username == Input.Username);

            if (user == null)
            {
                ErrorMessage = "用户名或密码错误";
                return Page();
            }

            // ===== 密码验证逻辑（修改点） =====
            bool passwordValid = false;

            // 优先使用 PasswordHash 字段验证（BCrypt 哈希）
            if (!string.IsNullOrEmpty(user.PasswordHash))
            {
                passwordValid = BCrypt.Net.BCrypt.Verify(Input.Password, user.PasswordHash);
            }
            // 若没有哈希，回退到明文比较（仅用于过渡，极不安全）
            else if (!string.IsNullOrEmpty(user.Password))
            {
                passwordValid = (Input.Password == user.Password);
            }

            if (!passwordValid)
            {
                ErrorMessage = "用户名或密码错误";
                return Page();
            }
            // ===== 验证结束 =====

            // 登录成功：写入 Session
            HttpContext.Session.SetInt32("UserId", user.Id);
            HttpContext.Session.SetString("Username", user.Username);

            // 登录成功：写入认证 Cookie（供 [Authorize] 使用）
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
            };
            var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(claimsIdentity),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
                });

            return RedirectToPage("/Profile");
        }

        public async Task<IActionResult> OnGetLogoutAsync()
        {
            HttpContext.Session.Clear();
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToPage("/Login");
        }

        public class LoginInputModel
        {
            [Required(ErrorMessage = "用户名不能为空")]
            public string Username { get; set; } = string.Empty;

            [Required(ErrorMessage = "密码不能为空")]
            public string Password { get; set; } = string.Empty;
        }
    }
}