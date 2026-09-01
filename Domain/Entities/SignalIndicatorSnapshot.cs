using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using CryptoSense.Domain.Enums;

namespace CryptoSense.Domain.Entities
{
    [Table("SignalIndicatorSnapshots")]
    public class SignalIndicatorSnapshot
    {
        [Key]
        public int Id { get; set; }
        
        public int SignalId { get; set; }
        
        [Required]
        [MaxLength(50)]
        public string IndicatorName { get; set; } = "";
        
        [Column(TypeName = "decimal(18, 8)")]
        public decimal Value { get; set; }
        
        public IndicatorVote Vote { get; set; } = IndicatorVote.Neutral;
        
        [Column(TypeName = "decimal(5, 2)")]
        public decimal Weight { get; set; }
    }
}
