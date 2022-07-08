import { MovieAPI } from "./MovieAPI";
import { useState, useEffect } from "react";
import { Link } from "react-router-dom";
import { Button, Checkbox, Input } from "antd";

function InsertMovieInput({ placeholder, name, movieState, setMovieState }) {
  function onChange(event) {
    const newState = {
      ...movieState,
    };

    newState[name] = event.target.value;
    setMovieState(newState);
  }

  return (
    <Input
      placeholder={placeholder}
      value={movieState[name]}
      onChange={onChange}
    />
  );
}

function InsertPage() {
  const [movieState, setMovieState] = useState({});

  async function nameMatch() {
    if (movieState.Title) {
      const movie = await MovieAPI.imdbApiLookupName(movieState.Title);
      if (movie) {
        mapMovie(movie);
      }
    }
  }
  async function insert() {}

  function mapMovie(movie) {
    const newState = {};
    newState.Title = movie.title;
    newState.Rating = movie.rating;
    newState.ReleaseDate = movie.releaseDate;
    newState.Runtime = movie.runtime;
    newState.Genre = movie.genre;
    newState.Director = movie.director;
    newState.Writer = movie.writer;
    newState.Actors = movie.actors;
    newState.Plot = movie.plot;
    newState.PosterLink = movie.posterLink;
    newState.imdbRating = movie.imdbRating;
    newState.imdbID = movie.imdbID;
    //newState.tomatoRating = movie.tomatoRating;

    setMovieState(newState);
  }

  async function imdbMatch() {
    if (movieState.imdbID) {
      const movie = await MovieAPI.imdbApiLookupImdbId(movieState.imdbID);
      if (movie) {
        mapMovie(movie);
      }
    }
  }

  return (
    <div>
      <div>{JSON.stringify(movieState)}</div>
      <table>
        <tbody>
          <tr>
            <td>Title</td>
            <td>
              <InsertMovieInput
                placeholder="Title"
                name="Title"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Rating</td>
            <td>
              <InsertMovieInput
                placeholder="Rating"
                name="Rating"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Release Date</td>
            <td>
              <InsertMovieInput
                placeholder="Release Date"
                name="ReleaseDate"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Runtime</td>
            <td>
              <InsertMovieInput
                placeholder="Runtime"
                name="Runtime"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Genre</td>
            <td>
              <InsertMovieInput
                placeholder="Genre"
                name="Genre"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Director</td>
            <td>
              <InsertMovieInput
                placeholder="Director"
                name="Director"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Writer</td>
            <td>
              <InsertMovieInput
                placeholder="Writer"
                name="Writer"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Actors</td>
            <td>
              <InsertMovieInput
                placeholder="Actors"
                name="Actors"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Plot</td>
            <td>
              <InsertMovieInput
                placeholder="Plot"
                name="Plot"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Poster Link</td>
            <td>
              <InsertMovieInput
                placeholder="Poster Link"
                name="PosterLink"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>IMDB Rating</td>
            <td>
              <InsertMovieInput
                placeholder="IMDB Rating"
                name="imdbRating"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>IMDB ID</td>
            <td>
              <InsertMovieInput
                placeholder="IMDB ID"
                name="imdbID"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
          <tr>
            <td>Tomato Rating</td>
            <td>
              <InsertMovieInput
                placeholder="Tomato Rating"
                name="tomatoRating"
                movieState={movieState}
                setMovieState={setMovieState}
              />
            </td>
          </tr>
        </tbody>
      </table>
      <Button onClick={imdbMatch} type="text">
        Attempt IMDB ID Match
      </Button>
      <Button onClick={nameMatch} type="text">
        Attempt Name Match
      </Button>
      <Button onClick={insert} type="primary">
        Insert
      </Button>
      <div id="imgContainer">
        <img alt="Poster" src={movieState.PosterLink}></img>
      </div>
    </div>
  );
}

export default InsertPage;
