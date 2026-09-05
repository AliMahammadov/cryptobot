using System.Threading.Tasks;
using CryptoSense.Domain.Entities;

namespace CryptoSense.Application.Interfaces
{
    public interface INewsService
    {
        Task<NewsSentimentSummary> GetNewsAndSentimentAsync();
        Task<System.Collections.Generic.List<CryptoNewsItem>> GetUrgentBreakingNewsAndListingsAsync();
    }
}
