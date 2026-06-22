using BCrypt.Net;
using daluandou.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using daluandou.Data;

namespace daluandou.Pages
{
    public class RegisterModel : PageModel
    {
        private readonly AppDbContext _context;

        public RegisterModel(AppDbContext context)
        {
            _context = context;
        }

        [BindProperty]
        public new User User { get; set; } = new User();

        public string? ErrorMessage { get; set; }

        public IActionResult OnGet()
        {
            return Page();
        }

        public async Task<IActionResult> OnPostAsync()
        {
            if (!ModelState.IsValid)
            {
                ErrorMessage = "表单填写有误，请检查！";
                return Page();
            }

            try
            {
                var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Username == User.Username);
                if (existingUser != null)
                {
                    ErrorMessage = "用户名已存在，请更换";
                    return Page();
                }

                var existingEmail = await _context.Users.FirstOrDefaultAsync(u => u.Email == User.Email);
                if (existingEmail != null)
                {
                    ErrorMessage = "邮箱已注册，请更换";
                    return Page();
                }

                var existingPhone = await _context.Users.FirstOrDefaultAsync(u => u.PhoneNumber == User.PhoneNumber);
                if (existingPhone != null)
                {
                    ErrorMessage = "手机号已注册，请更换";
                    return Page();
                }

                User.PasswordHash = BCrypt.Net.BCrypt.HashPassword(User.Password);
                User.CreateTime = DateTime.Now;
                _context.Users.Add(User);
                await _context.SaveChangesAsync();
                return RedirectToPage("/Login");
            }
            catch (Exception ex)
            {
                ErrorMessage = $"注册失败：{ex.Message}";
                return Page();
            }
        }
    }
}