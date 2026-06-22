using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace daluandou.Pages
{
    public class GameCells
    {
        public int Id { get; set; }
        public string? EventType { get; set; }
    }

}