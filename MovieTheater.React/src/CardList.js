/* eslint-disable jsx-a11y/anchor-is-valid */
/* eslint-disable jsx-a11y/anchor-has-content */

import { MovieAPI } from "./MovieAPI";
console.log(MovieAPI.getPosterThumbnail(100));

function CardList() {
  return (
    <div>
      <Card movieID="100" movieTitle="TestMovie" movieRating="R"></Card>
    </div>
  );
}

function Card({ movieID, movieTitle, movieRating }) {
  const thumbUrl = MovieAPI.getPosterThumbnail(movieID);
  return (
    <div>
      <img class="moviePosterImage" alt="" src={thumbUrl} />
      <a href="#" class="movieTitle">
        {movieTitle}
      </a>
      <span class="movieRating">{movieRating}</span>
      <br />
      <span class="movieTime"></span>
      <div class="actorSpacer"></div>
      <span class="movieActors">
        <a href="./browse?sort=Actor&actor="></a>
        <div class="actorSpacer"></div>
      </span>
      <div class="actorSpacer"></div>
      <div class="actorSpacer"></div>
      <span class="moviePlot"></span>
    </div>
  );
}

export default CardList;
