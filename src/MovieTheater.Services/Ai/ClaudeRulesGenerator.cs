using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using MovieTheater.Db;

namespace MovieTheater.Services.Ai
{
    public class ClaudeRulesGenerator
    {
        private const string SystemPrompt =
            "You are a board game expert. When given a board game's name and details, " +
            "list the rules that players most commonly get wrong or forget. " +
            "Be specific to this exact game — do not give generic advice. " +
            "Return ONLY a JSON object with a single field \"commonlyMissedRules\" containing a plain-text " +
            "bulleted list (each bullet on its own line starting with '• '). " +
            "If you are not confident about the game, say so briefly in that field rather than guessing.";

        private readonly AnthropicClient client;

        public ClaudeRulesGenerator(string apiKey)
        {
            client = new AnthropicClient { ApiKey = apiKey };
        }

        public async Task<string?> GenerateCommonlyMissedRulesAsync(Boardgame game)
        {
            var description = StripHtml(game.Description);
            var userContent =
                $"Game: {game.Name}" +
                (game.YearPublished.HasValue ? $" ({game.YearPublished})" : "") +
                (game.MinPlayers.HasValue || game.MaxPlayers.HasValue
                    ? $"\nPlayers: {game.MinPlayers}–{game.MaxPlayers}"
                    : "") +
                (game.AverageWeight.HasValue
                    ? $"\nComplexity: {game.AverageWeight:F2}/5"
                    : "") +
                (!string.IsNullOrWhiteSpace(description)
                    ? $"\nDescription: {description[..Math.Min(description.Length, 800)]}"
                    : "");

            var response = await client.Messages.Create(new MessageCreateParams
            {
                Model = "claude-sonnet-4-6",
                MaxTokens = 1024,
                System = new List<TextBlockParam>
                {
                    new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() }
                },
                Messages =
                [
                    new() { Role = Role.User, Content = userContent }
                ]
            });

            var text = response.Content
                .Select(b => b.Value)
                .OfType<TextBlock>()
                .FirstOrDefault()?.Text;

            if (string.IsNullOrWhiteSpace(text)) return null;

            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("commonlyMissedRules", out var field))
                    return field.GetString();
            }
            catch { }

            return text;
        }

        private static string StripHtml(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", "").Trim();
        }
    }
}
