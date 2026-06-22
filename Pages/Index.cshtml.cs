using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace daluandou.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        public void OnGet()
        {

        }
    }
}