using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MovieTheater.Services
{
    internal class ServicesUtil
    {
        public static string CleanTitle(string movieName)
        {
            if (string.IsNullOrWhiteSpace(movieName))
            {
                return string.Empty;
            }

            // First remove any quality (720p) designation from the final portion of the folder name
            string[] parts = movieName.Split(' ');
            string qual = parts.Last().ToLowerInvariant();
            if (qual.Length > 0 && qual.Last() == 'p')
            {
                // Remove only the last occurrence (token) to avoid accidental removals elsewhere
                int lastIndex = movieName.LastIndexOf(qual, StringComparison.OrdinalIgnoreCase);
                if (lastIndex >= 0)
                {
                    movieName = movieName.Remove(lastIndex, qual.Length).Trim();
                }
            }

            // Next remove any tags between square brackets in movie name
            movieName = Regex.Replace(movieName, @"\[[^\]]*?\]", string.Empty);
            movieName = movieName.Trim();
            return movieName;
        }
    }
}
