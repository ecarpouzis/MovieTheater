import { useCallback, useEffect, useRef, useState } from "react";
import { Spin } from "antd";
import { MovieAPI } from "../../MovieAPI";
import useInfiniteScroll from "../../hooks/useInfiniteScroll";
import PhotoGrid from "./PhotoGrid";

// The folder browser (docs/photos-plan.md §2.9): the Path tree, with zero extra modeling. It is a
// browse VIEW — a folder is never an album's identity, so the disk layout stays free to be ugly.
//
// Unlike the timeline it does not group by date and it shows the copies a duplicate group collapses:
// it answers "what is actually in this folder", which is the question the tree is here for.
//
// Hidden items are the Phase 4 exception. "Hidden is visible only to an admin" has to hold on every
// surface or it holds on none — a folder tab that quietly opted out would not be a rule, it would be
// a longer route to the same pictures. The server ignores includeHidden from anyone else.

export default function PhotoFolders({ onOpen, selection, onMakeAlbum, includeHidden = false }) {
  const [path, setPath] = useState("");
  const [folders, setFolders] = useState([]);
  const [items, setItems] = useState([]);
  const [state, setState] = useState("loading");
  const [hasMore, setHasMore] = useState(false);
  const skipRef = useRef(0);
  const hasMoreRef = useRef(false);
  const inFlightRef = useRef(false);

  const load = useCallback(async (targetPath, append) => {
    if (inFlightRef.current) return;
    if (append && !hasMoreRef.current) return;
    inFlightRef.current = true;
    try {
      const response = await MovieAPI.getPhotosFolder({
        path: targetPath,
        skip: append ? skipRef.current : 0,
        includeHidden,
      });
      if (!response.ok) {
        setState("error");
        return;
      }
      const body = await response.json();
      setFolders(body.folders || []);
      setItems((prev) => (append ? prev.concat(body.items || []) : body.items || []));
      skipRef.current = (append ? skipRef.current : 0) + (body.items?.length || 0);
      hasMoreRef.current = !!body.hasMore;
      setHasMore(!!body.hasMore);
      setState("ready");
    } catch {
      setState("error");
    } finally {
      inFlightRef.current = false;
    }
  }, [includeHidden]);

  useEffect(() => {
    setState("loading");
    skipRef.current = 0;
    hasMoreRef.current = false;
    setHasMore(false);
    load(path, false);
  }, [path, load]);

  const { sentinelRef, recheck } = useInfiniteScroll({
    enabled: state === "ready",
    hasMore,
    onLoadMore: () => load(path, true),
  });

  useEffect(() => {
    recheck();
  }, [items.length, recheck]);

  const segments = path ? path.split("/") : [];
  const folderName = segments.length ? segments[segments.length - 1] : "";

  return (
    <div className="photo-folders">
      <nav className="photo-crumbs">
        <button type="button" className="photo-crumb" onClick={() => setPath("")}>
          All folders
        </button>
        {segments.map((segment, index) => (
          <button
            type="button"
            className="photo-crumb"
            key={segment + index}
            onClick={() => setPath(segments.slice(0, index + 1).join("/"))}
          >
            {segment}
          </button>
        ))}
      </nav>

      {path && onMakeAlbum && (
        // §2.9's seed action. It COPIES the folder's membership into album rows — the folder itself
        // is never the album's identity, so the disk layout stays free to be as ugly as it is.
        <button type="button" className="photos-button" onClick={() => onMakeAlbum(path, folderName)}>
          Make an album from this folder
        </button>
      )}

      {state === "loading" && <Spin />}
      {state === "error" && <p className="photos-note">Could not read that folder.</p>}

      {state === "ready" && (
        <>
          {folders.length > 0 && (
            <ul className="photo-folder-list">
              {folders.map((folder) => (
                <li key={folder.name}>
                  <button
                    type="button"
                    className="photo-folder"
                    onClick={() => setPath(path ? `${path}/${folder.name}` : folder.name)}
                  >
                    <span className="photo-folder-name">{folder.name}</span>
                    <span className="photo-folder-count">{folder.count.toLocaleString()}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}

          <PhotoGrid
            items={items}
            groupBySection={false}
            onOpen={onOpen}
            selection={selection}
            emptyText="No files directly in this folder."
          />
          <div ref={sentinelRef} className="photos-sentinel">
            {hasMore && <Spin size="small" />}
          </div>
        </>
      )}
    </div>
  );
}
