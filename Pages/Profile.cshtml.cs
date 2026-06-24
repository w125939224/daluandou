using daluandou.Data;
using daluandou.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace daluandou.Pages
{
    [Authorize]
    public class ProfileModel : PageModel
    {
        private readonly AppDbContext _context;

        public ProfileModel(AppDbContext context)
        {
            _context = context;
        }

        public User CurrentUser { get; set; } = new User();

        [TempData]
        public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                var userId = HttpContext.Session.GetInt32("UserId");
                var username = HttpContext.Session.GetString("Username");

                if (userId == null || string.IsNullOrEmpty(username))
                {
                    return RedirectToPage("/Login");
                }

                CurrentUser = await _context.Users
                    .FirstOrDefaultAsync(u => u.Id == userId);

                if (CurrentUser == null)
                {
                    HttpContext.Session.Clear();
                    ErrorMessage = "用户不存在，请重新登录";
                    return RedirectToPage("/Login");
                }

                return Page();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载失败：{ex.Message}";
                return RedirectToPage("/Error");
            }
        }
    }
}