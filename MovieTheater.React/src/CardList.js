/* eslint-disable jsx-a11y/anchor-is-valid */
/* eslint-disable jsx-a11y/anchor-has-content */

import { MovieAPI } from "./MovieAPI";
console.log(MovieAPI.getPosterThumbnail(100));

function CardList() {
  return (
    <div>
      <Card
        movieID="100"
        movieTitle="TestMovie"
        movieRating="R"
        movieTime="1h 30m"
        movieActors={["Eric Carpouzis", "Neil Bostian"]}
      ></Card>
    </div>
  );
}

function Card({ movieID, movieTitle, movieRating, movieTime, movieActors }) {
  const thumbUrl = MovieAPI.getPosterThumbnail(movieID);

  const actorList = movieActors.map((actor) => (
    <div>
      <a href={"/browse?sort=Actor&actor=" + actor.trim()}>{actor}</a>
      <div class="actorSpacer"></div>
    </div>
  ));

  return (
    <div>
      <img class="moviePosterImage" alt="" src={thumbUrl} />
      <a href="#" class="movieTitle">
        {movieTitle}
      </a>
      <span class="movieRating">{movieRating}</span>
      <br />
      <span class="movieTime">{movieTime}</span>
      {actorList}
      <div class="actorSpacer"></div>
      <div class="actorSpacer"></div>
      <span class="moviePlot"></span>
    </div>
  );
}

export default CardList;
