using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace daluandou.Pages
{
    public class GameCard
    {
        public int Id { get; set; }
        public string CardName { get; set; }
        public string CardType { get; set; }
        public string CardRarity { get; set; }
        public string CardDescription { get; set; }
        public string EffectType { get; set; }
        public int EffectValue { get; set; }
        public int Duration { get; set; }
        public string TargetType { get; set; }
        public string CostType { get; set; }
        public int CostValue { get; set; }
        public DateTime CreatedAt { get; set; }
    }

}