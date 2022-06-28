# Movie Theater Site

 I've always been a big fan of cinema, and streaming services have made it easier than ever to share movies with friends. I use software such as [Plex](https://www.plex.tv/) and [SyncLounge](https://synclounge.tv/) to have an online movie nights. I wanted a tool to track movies we had access to watch, while letting me develop useful custom features. This site was first built in ASP.Net MVC, then migrated to .Net Core. A new front-end driven by React is currently underway, and can be seen in the React branch.

![Site Preview](./README/SitePreviewImage.png)

 A brief list of features included in the site:

 * Movie Data & Posters - Data has been retrieved through various methods including web scraping and API access. Posters were once stored as BLOBs, but I found it faster and less resource-intensive to store them as files. When a new movie is added to the site a Python script is ran to create a high quality/small sized thumbnail, which is used when browsing for movies.

 * Users - A typical ASP.Net Identity implementation would be trivial, but this site is communally shared between a group of friends with no private data. After initial design discussions, I decided to create a very simple user implementation that does not require passwords for login. It's thus trivial for any user to check or update another user's information, which matches the intent of the site.

 * Has-Watched List - Once logged in, a user can mark each movie they've watched. They can then see the number of movies they've ever watched, or filter to see the information for each of those movies.

 * Want-To-Watch List - Similar to the above feature, but used for movies you want to watch in the future. This allows me to find movies multiple people want to see when picking a movie for movie night.

 * Movie Rating - Users are able to rate movies and compare to the ratings from other users.

 * Collage - The site can generate one massive collage of all movie posters. Eventually I plan to add the ability to generate mosaics from movie posters. Example image:

![Collage Preview](./README/CollagePreviewImage.png) (Note: The full collage is much larger, coming in at a massive 1,875px x 21,500px)

# Local Development

Configure C# project
1. Copy `src/MovieTheater/appsettings.default.json` to `src/MovieTheater/appsettings.Development.json`
1. Update the values in appsettings.Development for local development:
    - MoviePostersDir points to a directory we attempt to find movie poster images. This can be an empty dir if you don't need local dev.
    - DbConnectionString is your sql server connection string to the movie db
1. Setup `MovieTheater` c# project to launch on port 3001
    - Set env var `ASPNETCORE_URLS=http://+:3001`
    - And/Or, setup `src/MovieTheater/Properties/launchSettings.json` with an applicationUrl `"http://localhost:3001"`
    - ![launchSettings.json image](./README/launchSettings.json.png)

Configure React.js project
1. In console, `cd src/ui`
1. `npm install`
1. `npm run start`

The c# project includes a reverse proxy that serves the frontend files for both local and deployed dev. React project should start on localhost:3000, c# should start on localhost:3001.