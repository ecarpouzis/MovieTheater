import { MovieAPI } from "../MovieAPI";
import { useState, useEffect } from "react";
import { Link } from "react-router-dom";
import { Button, Checkbox, Input, message } from "antd";

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

function MovieInsertForm({ movie, setMovie }) {
  async function insert() {
    if (movie.title) {
      try {
        const result = await MovieAPI.insertMovie(movie);
        const data = await result.json();
        if (!data.success) {
          message.error("Error: " + data.message);
        } else {
          message.success("Movie inserted successfully.");
        }
      } catch (err) {
        message.error(err.message || "Failed to insert movie.");
      }
    }
  }

  return (
    <div>
      <div class="imgContainer">
        <img alt="Poster" src={movie.posterLink}></img>
      </div>
      <div>
        <table>
          <tbody>
            <tr>
              <td>Title</td>
              <td>
                <InsertMovieInput placeholder="Title" name="title" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Simple Title</td>
              <td>
                <InsertMovieInput placeholder="Simple Title" name="simpleTitle" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Rating</td>
              <td>
                <InsertMovieInput placeholder="Rating" name="rating" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Release Date</td>
              <td>
                <InsertMovieInput placeholder="Release Date" name="releaseDate" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Runtime</td>
              <td>
                <InsertMovieInput placeholder="Runtime" name="runtime" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Genre</td>
              <td>
                <InsertMovieInput placeholder="Genre" name="genre" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Director</td>
              <td>
                <InsertMovieInput placeholder="Director" name="director" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Writer</td>
              <td>
                <InsertMovieInput placeholder="Writer" name="writer" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Actors</td>
              <td>
                <InsertMovieInput placeholder="Actors" name="actors" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Plot</td>
              <td>
                <InsertMovieInput placeholder="Plot" name="plot" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Poster Link</td>
              <td>
                <InsertMovieInput placeholder="Poster Link" name="posterLink" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>IMDB Rating</td>
              <td>
                <InsertMovieInput placeholder="IMDB Rating" name="imdbRating" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>IMDB ID</td>
              <td>
                <InsertMovieInput placeholder="IMDB ID" name="imdbID" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>Tomato Rating</td>
              <td>
                <InsertMovieInput placeholder="Tomato Rating" name="tomatoRating" movieState={movie} setMovieState={setMovie} />
              </td>
            </tr>
            <tr>
              <td>
                <Button onClick={insert} type="primary">
                  Insert
                </Button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>
  );
}

function BatchInsertPage() {
  const [batchTextAreaState, setBatchTextAreaState] = useState();
  const [movies, setMovies] = useState([]);

  function setMovie(movie) {
    const newMovies = movies.map((m) => {
      if (m.index == movie.index) {
        return { ...movie };
      }
      return m;
    });
    setMovies(newMovies);
  }

  async function setupMovieList() {
    const movieNames = batchTextAreaState
      .split("\n")
      .map((name) => name.trim())
      .filter((name) => name.length > 0);
    const moviesArray = await MovieAPI.movieLookupFromNames(movieNames);

    setMovies(
      moviesArray.map((movie, index) => {
        return {
          index,
          ...movie,
        };
      })
    );
  }

  return (
    <div>
      <div>
        <Input.TextArea onChange={(e) => setBatchTextAreaState(e.target.value)}></Input.TextArea>
        <Button onClick={setupMovieList}>Generate Batch</Button>
      </div>
      {movies.map((movie) => (
        <MovieInsertForm movie={movie} setMovie={setMovie} />
      ))}
    </div>
  );
}

export default BatchInsertPage;
