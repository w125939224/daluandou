using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace daluandou.Pages
{
    public class LogoutModel : PageModel
    {
        public async Task<IActionResult> OnGetAsync()
        {
            try
            {
                HttpContext.Session.Clear();
                await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                if (Request.Cookies.ContainsKey(CookieAuthenticationDefaults.CookiePrefix + "CookieAuth"))
                {
                    Response.Cookies.Delete(CookieAuthenticationDefaults.CookiePrefix + "CookieAuth");
                }
                TempData["SuccessMessage"] = "已成功退出登录！";
                return RedirectToPage("/Login");
            }
            catch (Exception ex)
            {
                TempData["ErrorMessage"] = $"退出失败：{ex.Message}";
                return RedirectToPage("/Login");
            }
        }
    }
}