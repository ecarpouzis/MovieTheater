/* eslint-disable jsx-a11y/anchor-is-valid */
/* eslint-disable jsx-a11y/anchor-has-content */
function CardList() {
  return (
    <div>
      <Card movieTitle="TestMovie" movieRating="R"></Card>
    </div>
  );
}

function Card({ movieTitle, movieRating }) {
  return (
    <div>
      <img class="moviePosterImage" alt="" src="" />
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
