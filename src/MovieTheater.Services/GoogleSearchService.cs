using HtmlAgilityPack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace MovieTheater.Services
{
    public class GoogleSearchService
    {
        public async Task<string?> FindImdbIdFromMovieName(string movieName)
        {
            //First remove any quality (720p) designation from the final portion of the folder name
            string qual = movieName.Split(' ').Last<string>().ToLower();
            if (qual.Last<char>() == 'p')
            {
                movieName = movieName.Replace(qual, "");
            }

            //Next remove any tags between square brackets in movie name
            movieName = Regex.Replace(movieName, @"\[.*?\]", "");

            movieName = movieName.Trim();

            ////Try IMDB page lookup.
            //Search using Google:         
            string GoogleRT = "http://www.google.com/search?num=1&q=" + HttpUtility.UrlEncode(movieName) + " IMDB";
            var result = await new HtmlWeb().LoadFromWebAsync(GoogleRT);
            var googleNodes = result.DocumentNode.SelectNodes("//html//body//div[@id='main']//a").ToList();
            foreach(var link in googleNodes)
            {
                var href = link.GetAttributeValue("href", "");
                var match = Regex.Match(href, @"https:\/\/www\.imdb\.com\/title\/([^\/]+).*");
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }

            return null;

            ////I notice URLs returned this way have additional text. Split on = and remove the extra "&amp" from the href
            //string[] imdbLink = googleNode.Attributes["href"].Value.Split('=')[1].Split(new string[] { "&amp;" }, StringSplitOptions.None)[0].Split('/');
            //string imdbID = imdbLink[imdbLink.Length - 2];
            //
            //return imdbID;
        }   //
    }
}
