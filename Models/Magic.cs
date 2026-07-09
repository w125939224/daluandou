using System.ComponentModel.DataAnnotations;

namespace daluandou.Models
{
    public class Magic
    {
        [Key]
        public int Id { get; set; }
        public string Name { get; set; }

        public int MpCost { get; set; }

        public int Range { get; set; }

        public string EffectType { get; set; } = "Scaling";

        public int BaseValue { get; set; }

        public string Description { get; set; }
    }
}