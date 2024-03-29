import { MovieAPI } from "../MovieAPI";
import { useState, useEffect } from "react";
import { Link } from "react-router-dom";
import { Button, Input, Radio } from "antd";

function InsertMovieInput({ placeholder, name, movieState, setMovieState }) {
  function onChange(event) {
    const newState = {
      ...movieState,
    };

    newState[name] = event.target.value;
    setMovieState(newState);
  }

  return <Input placeholder={placeholder} value={movieState[name]} onChange={onChange} />;
}

function InsertPage() {
  const [movieState, setMovieState] = useState({});

  async function insert() {
    if (movieState.title) {
      await MovieAPI.insertMovie(movieState);
    }
  }

  async function imdbMatch() {
    if (movieState.imdbID) {
      let movie = await MovieAPI.omdbLookupImdbID(movieState.imdbID);
      if (movie) {
        setMovieState(movie);
      }
    }
  }

  async function nameMatch() {
    if (movieState.title) {
      let movie = await MovieAPI.omdbLookupName(movieState.title);
      if (movie) {
        setMovieState(movie);
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
              <InsertMovieInput placeholder="Title" name="title" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Simple Title</td>
            <td>
              <InsertMovieInput placeholder="Simple Title" name="simpleTitle" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Rating</td>
            <td>
              <InsertMovieInput placeholder="Rating" name="rating" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Release Date</td>
            <td>
              <InsertMovieInput placeholder="Release Date" name="releaseDate" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Runtime</td>
            <td>
              <InsertMovieInput placeholder="Runtime" name="runtime" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Genre</td>
            <td>
              <InsertMovieInput placeholder="Genre" name="genre" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Director</td>
            <td>
              <InsertMovieInput placeholder="Director" name="director" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Writer</td>
            <td>
              <InsertMovieInput placeholder="Writer" name="writer" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Actors</td>
            <td>
              <InsertMovieInput placeholder="Actors" name="actors" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Plot</td>
            <td>
              <InsertMovieInput placeholder="Plot" name="plot" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Poster Link</td>
            <td>
              <InsertMovieInput placeholder="Poster Link" name="posterLink" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>IMDB Rating</td>
            <td>
              <InsertMovieInput placeholder="IMDB Rating" name="imdbRating" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>IMDB ID</td>
            <td>
              <InsertMovieInput placeholder="IMDB ID" name="imdbID" movieState={movieState} setMovieState={setMovieState} />
            </td>
          </tr>
          <tr>
            <td>Tomato Rating</td>
            <td>
              <InsertMovieInput placeholder="Tomato Rating" name="tomatoRating" movieState={movieState} setMovieState={setMovieState} />
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
        <img alt="Poster" src={movieState.posterLink}></img>
      </div>
    </div>
  );
}

export default InsertPage;
