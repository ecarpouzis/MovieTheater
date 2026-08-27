/**
 * ComicVine — the two scrapes (series volumes, then issues) and the Open Library / Google Books
 * fallback. The API key is plain host configuration (the Config tab); with no key the scrapers run
 * cache-first only and never open a socket.
 */
import { useQuery } from "@tanstack/react-query";
import { Alert } from "antd";
import { bk } from "../../booksQuery";
import { comicVineStart, comicVineStatus, externalStart } from "../adminApi";
import JobCard from "../JobCard";

export default function ComicVineTab() {
  const status = useQuery({ queryKey: bk.admin("comicvine-status"), queryFn: ({ signal }) => comicVineStatus(signal), refetchInterval: 10000 });
  return (
    <div className="adm-tab">
      {status.data && !status.data.configured && (
        <Alert type="warning" showIcon title="No ComicVine API key is set." description="Set ComicVineApiKey on the Config tab (or Books:ComicVineApiKey in the host's configuration). Until then both scrapes are cache-first and make no network calls." />
      )}
      <JobCard kind="comicvine:series" title="ComicVine — series" description="Matches each parsed series key to a ComicVine volume and stores the candidates. Run this before the issues pass." start={() => comicVineStart("series")} />
      <JobCard kind="comicvine:issues" title="ComicVine — issues" description="Fetches the issues of every linked volume (covers dates, titles, decks)." start={() => comicVineStart("issues")} />
      <JobCard kind="external" title="Open Library / Google Books" description="The fallback for books and for comics ComicVine does not know: a work record, authors, a description." start={externalStart} />
    </div>
  );
}
