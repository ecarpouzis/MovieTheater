import { useCallback, useEffect, useState } from "react";
import { Input, Modal, Spin, message } from "antd";
import { MovieAPI } from "../../MovieAPI";
import PhotoGrid from "./PhotoGrid";
import { formatTaken } from "./PhotoLightbox";

// People (docs/photos-plan.md §2.8): the family's person list, and a page per person.
//
// Two lists, because they are two different things. A NAMED row is a person — with a page, a tag
// count and an optional birth year that hints at dates (§2.7). A row with an empty name is a face
// cluster the Immich sync imported and nobody has claimed: "an unnamed group of N faces". Naming one
// is the single highest-leverage action in the whole feature, because its suggestions are already
// pointed at that row — the moment it has a name, they are suggestions about a person.
//
// Everything here is member-visible and member-editable. A shared family album whose people only one
// person could edit would be one person's album.

// Which person you are looking at is a ROUTE (/photos/people/:id), not component state: a person's
// page is the most linkable thing in the album, and it used to vanish on a refresh.
export default function PhotoPeople({
  people,
  unnamed,
  loading,
  onReload,
  onOpenAsset,
  onChanged,
  personId,
  onOpenPerson,
  onBackToPeople,
}) {
  const [editing, setEditing] = useState(null);
  const [naming, setNaming] = useState(null);

  if (personId) {
    return (
      <PhotoPersonPage
        id={personId}
        onBack={onBackToPeople}
        onOpen={onOpenAsset}
        onOpenPerson={onOpenPerson}
      />
    );
  }

  if (loading) return <Spin />;

  return (
    <div className="photo-people">
      <div className="photo-people-head">
        <h2 className="photos-panel-head">People</h2>
        <button type="button" className="photos-button" onClick={() => setEditing({ name: "", birthYear: "" })}>
          Add a person
        </button>
      </div>

      {people.length === 0 && unnamed.length === 0 && (
        <p className="photos-note">
          Nobody yet. Add a person here, or tag a face from the lightbox — the name is created for you.
        </p>
      )}

      <ul className="photo-people-list">
        {people.map((person) => (
          <li key={person.id} className="photo-person-card">
            <button type="button" className="photo-person-open" onClick={() => onOpenPerson?.(person.id)}>
              {person.faceCropUrl || person.coverUrl ? (
                <img className="photo-person-face" src={person.faceCropUrl || person.coverUrl} alt="" />
              ) : (
                <span className="photo-person-face placeholder" aria-hidden="true" />
              )}
              <span className="photo-person-label">
                <span className="photo-person-name-text">{person.name}</span>
                <span className="photo-person-meta">
                  {person.tagCount.toLocaleString()} photo{person.tagCount === 1 ? "" : "s"}
                  {person.birthYear ? ` · born ${person.birthYear}` : ""}
                  {person.suggestionCount ? ` · ${person.suggestionCount} suggested` : ""}
                </span>
              </span>
            </button>
            <button
              type="button"
              className="photos-button"
              onClick={() => setEditing({ ...person, birthYear: person.birthYear ?? "" })}
            >
              Edit
            </button>
          </li>
        ))}
      </ul>

      {unnamed.length > 0 && (
        <div className="photo-people-unnamed">
          <h3 className="photos-panel-head">Faces waiting for a name</h3>
          <p className="photos-note">
            The photo sidecar grouped these faces together but has no idea who they are — and it never
            will: names live here, not there. Naming one group tags every photo it appears in, all at
            once.
          </p>
          <ul className="photo-people-list">
            {unnamed.map((group) => (
              <li key={group.id} className="photo-person-card">
                {group.faceCropUrl ? (
                  <img className="photo-person-face" src={group.faceCropUrl} alt="" />
                ) : (
                  <span className="photo-person-face placeholder" aria-hidden="true" />
                )}
                <span className="photo-person-label">
                  <span className="photo-person-name-text">Unnamed group</span>
                  <span className="photo-person-meta">
                    {group.suggestionCount.toLocaleString()} face
                    {group.suggestionCount === 1 ? "" : "s"}
                  </span>
                </span>
                <button type="button" className="photos-button" onClick={() => setNaming(group)}>
                  Name or map
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}

      <PersonEditor
        value={editing}
        onClose={() => setEditing(null)}
        onSaved={() => {
          setEditing(null);
          onReload?.();
          onChanged?.();
        }}
      />

      <NameClusterModal
        group={naming}
        people={people}
        onClose={() => setNaming(null)}
        onDone={() => {
          setNaming(null);
          onReload?.();
          onChanged?.();
        }}
      />
    </div>
  );
}

/** Create or edit: a name, an optional birth year (which only ever HINTS at dates — §2.7), and the
 *  delete that needs an explicit confirmation because tags are irreplaceable labor (§2.11). */
function PersonEditor({ value, onClose, onSaved }) {
  const [name, setName] = useState("");
  const [birthYear, setBirthYear] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    setName(value?.name || "");
    setBirthYear(value?.birthYear === 0 || value?.birthYear ? String(value.birthYear) : "");
  }, [value]);

  if (!value) return null;
  const creating = !value.id;

  const save = async () => {
    if (!name.trim()) return;
    setBusy(true);
    try {
      const year = birthYear.trim() === "" ? null : Number(birthYear.trim());
      const response = creating
        ? await MovieAPI.createPhotoPerson({ name: name.trim(), birthYear: year })
        : await MovieAPI.updatePhotoPerson(value.id, {
            name: name.trim(),
            birthYear: year,
            birthYearSet: true,
          });
      if (!response.ok) {
        message.error("Could not save that.");
        return;
      }
      onSaved?.();
    } finally {
      setBusy(false);
    }
  };

  const remove = async () => {
    setBusy(true);
    try {
      const response = await MovieAPI.deletePhotoPerson(value.id);
      if (!response.ok) {
        message.error("Could not delete that person.");
        return;
      }
      message.success("Deleted. No photo was touched.");
      onSaved?.();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal className="photos-modal" open onCancel={onClose} footer={null} title={creating ? "Add a person" : "Edit person"} destroyOnHidden>
      <div className="photo-person-editor">
        <label className="photo-field">
          <span>Name</span>
          <Input value={name} onChange={(e) => setName(e.target.value)} onPressEnter={save} disabled={busy} />
        </label>
        <label className="photo-field">
          <span>Birth year (optional)</span>
          <Input
            value={birthYear}
            onChange={(e) => setBirthYear(e.target.value.replace(/[^0-9]/g, ""))}
            disabled={busy}
            placeholder="e.g. 1978"
          />
        </label>
        <p className="photos-note">
          A birth year is only ever a hint when dating old photos — it suggests bounds and never writes
          a date by itself.
        </p>
        <div className="photo-person-editor-actions">
          <button type="button" className="photos-button" disabled={busy || !name.trim()} onClick={save}>
            Save
          </button>
          {!creating && (
            <button type="button" className="photos-button" disabled={busy} onClick={remove}>
              Delete person
            </button>
          )}
        </div>
      </div>
    </Modal>
  );
}

/** Name an imported cluster, or MAP it onto somebody who already exists (§2.8). Mapping merges rather
 *  than creating a second row for the same face — and the merge resolves collisions in favour of the
 *  stronger claim, so it can never weaken a human's tag into a machine's guess. */
function NameClusterModal({ group, people, onClose, onDone }) {
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    setName("");
  }, [group]);

  if (!group) return null;

  const nameIt = async () => {
    if (!name.trim()) return;
    setBusy(true);
    try {
      const response = await MovieAPI.updatePhotoPerson(group.id, { name: name.trim() });
      if (!response.ok) {
        message.error("Could not name that group.");
        return;
      }
      message.success(`Named. Every suggestion for this face is now about ${name.trim()}.`);
      onDone?.();
    } finally {
      setBusy(false);
    }
  };

  const mapTo = async (person) => {
    setBusy(true);
    try {
      const response = await MovieAPI.mergePhotoPerson(group.id, person.id);
      if (!response.ok) {
        message.error("Could not map that group.");
        return;
      }
      const body = await response.json();
      message.success(`Mapped onto ${person.name} — ${body.moved} suggestion(s) moved across.`);
      onDone?.();
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal className="photos-modal" open onCancel={onClose} footer={null} title="Who is this?" destroyOnHidden>
      <div className="photo-album-picker">
        <p className="photos-note">
          {group.suggestionCount.toLocaleString()} face
          {group.suggestionCount === 1 ? "" : "s"} were grouped together. Give the group a name, or map
          it onto someone already here.
        </p>
        <div className="photo-album-new">
          <Input
            placeholder="Name this person"
            value={name}
            onChange={(e) => setName(e.target.value)}
            onPressEnter={nameIt}
            disabled={busy}
          />
          <button type="button" className="photos-button" disabled={busy || !name.trim()} onClick={nameIt}>
            Name
          </button>
        </div>
        {people.length > 0 && (
          <ul className="photo-album-list">
            {people.map((person) => (
              <li key={person.id}>
                <button type="button" className="photo-album-choice" disabled={busy} onClick={() => mapTo(person)}>
                  <span>{person.name}</span>
                  <span className="photo-album-count">{person.tagCount}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>
    </Modal>
  );
}

/** One person's page: their photos, and the "also with…" chips that make a family album navigable by
 *  who is in it rather than only by when it happened (§2.8). */
function PhotoPersonPage({ id, onBack, onOpen, onOpenPerson }) {
  const [detail, setDetail] = useState(null);
  const [items, setItems] = useState([]);
  const [state, setState] = useState("loading");
  const [hasMore, setHasMore] = useState(false);

  const load = useCallback(
    async (skip) => {
      try {
        const [detailResponse, pageResponse] = await Promise.all([
          skip === 0 ? MovieAPI.getPhotoPerson(id) : Promise.resolve(null),
          MovieAPI.getPhotoPersonTimeline(id, { skip, take: 120 }),
        ]);
        if (detailResponse && detailResponse.ok) setDetail(await detailResponse.json());
        if (!pageResponse.ok) {
          setState("error");
          return;
        }
        const body = await pageResponse.json();
        setItems((prev) => (skip === 0 ? body.items || [] : prev.concat(body.items || [])));
        setHasMore(!!body.hasMore);
        setState("ready");
      } catch {
        setState("error");
      }
    },
    [id]
  );

  useEffect(() => {
    setState("loading");
    setItems([]);
    load(0);
  }, [load]);

  if (state === "loading") return <Spin />;
  if (state === "error") return <p className="photos-note">Could not load that person.</p>;

  const person = detail?.person;
  return (
    <div className="photo-person-page">
      <div className="photo-people-head">
        <button type="button" className="photos-button" onClick={onBack}>
          ← People
        </button>
        <h2 className="photos-panel-head">{person?.name}</h2>
      </div>

      <p className="photos-subtitle">
        {(detail?.tagCount ?? 0).toLocaleString()} photo{detail?.tagCount === 1 ? "" : "s"}
        {person?.birthYear ? ` · born ${person.birthYear}` : ""}
        {detail?.firstTakenAt
          ? ` · ${formatTaken({ takenAt: detail.firstTakenAt })} – ${formatTaken({ takenAt: detail.lastTakenAt })}`
          : ""}
        {detail?.suggestionCount ? ` · ${detail.suggestionCount} suggestion(s) waiting` : ""}
      </p>

      {detail?.alsoWith?.length > 0 && (
        <div className="photo-person-chips">
          <span className="photos-note">Also with:</span>
          {detail.alsoWith.map((other) => (
            <button
              key={other.id}
              type="button"
              className="photo-person-chip"
              onClick={() => onOpenPerson?.(other.id)}
            >
              {other.name} <span className="photo-person-count">{other.count}</span>
            </button>
          ))}
        </div>
      )}

      <PhotoGrid items={items} groupBySection onOpen={onOpen} emptyText="No photos tagged with this person yet." />

      {hasMore && (
        <div className="photo-person-more">
          <button type="button" className="photos-button" onClick={() => load(items.length)}>
            Load more
          </button>
        </div>
      )}
    </div>
  );
}
