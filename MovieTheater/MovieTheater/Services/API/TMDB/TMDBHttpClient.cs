using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace MovieTheater.Services.API
{
    public class TMDBHttpClient
    {
        private readonly Uri baseUri;

        private readonly HttpClient httpClient;

        private readonly string accessTokenv3 = "4236d2af8570e611cdd7ddb4b8637d98";
        private readonly string accessTokenv4 = "eyJhbGciOiJIUzI1NiJ9.eyJhdWQiOiI0MjM2ZDJhZjg1NzBlNjExY2RkN2RkYjRiODYzN2Q5OCIsInN1YiI6IjRmZDEwNWRkMTljMjk1NGQzYTAwMDNkYyIsInNjb3BlcyI6WyJhcGlfcmVhZCJdLCJ2ZXJzaW9uIjoxfQ.5jdAKl-jzSunTMk14W9IQgdnPX4GR9BbM3N0pT9q1gY";

  //      curl  --request GET \
  //            --url 'https://api.themoviedb.org/3/movie/76341' \
  //            --header 'Authorization: Bearer <<access_token>>' \
  //            --header 'Content-Type: application/json;charset=utf-8'


        private DateTime? accessTokenTimestamp = null;

        public TMDBHttpClient()
        {
            baseUri = new Uri($"https://api.themoviedb.org");
            httpClient = new HttpClient();
            httpClient.BaseAddress = baseUri;
        }

        public async Task RefreshAccessToken()
        {
            //var msg = new HttpRequestMessage(HttpMethod.Get, baseUri + "/3/movie/");
            var msg = new HttpRequestMessage(HttpMethod.Get, "/3/search/movie?api_key="+accessTokenv3+"&query=dory&page=1&include_adult=false");
            msg.Headers.Add("Authorization","Bearer "+ accessTokenv3);
            msg.Content = new StringContent("");
            msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
           
            HttpResponseMessage response = await httpClient.SendAsync(msg);
            var content = await response.Content.ReadAsStringAsync();

        }

    }
}
