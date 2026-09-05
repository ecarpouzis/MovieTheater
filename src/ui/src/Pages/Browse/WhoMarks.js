import { memo } from "react";
import { Tooltip } from "antd";
import { EyeFilled, HeartFilled } from "@ant-design/icons";
import "./WhoMarks.css";

/**
 * Who has seen / wants to watch a title — the counts pill on the poster's corner (2026-09-05, Eric's
 * pick "B · Counts"): one quiet mono pill, an eye with a number and a heart with a number, constant
 * size whatever the crowd; the names are one hover (or tap) away. Nothing drawn when nobody has
 * marked it. Zero card height: it sits INSIDE the poster, the board-game expansion-flag idea.
 *
 * `marks` = { seen: [names], want: [names] } for the people the viewer wants shown (the ⚙ panel's
 * "Friends’ marks" lever: Off · Wants only · Seen + wants — the caller has already applied it).
 */
const WhoMarks = memo(function WhoMarks({ marks, large = false }) {
  const seen = marks?.seen ?? [];
  const want = marks?.want ?? [];
  if (!seen.length && !want.length) return null;
  const title = (
    <div className="who-marks-tip">
      {seen.length > 0 && <div><span className="who-marks-tip-seen">Seen</span> · {seen.join(", ")}</div>}
      {want.length > 0 && <div><span className="who-marks-tip-want">Wants to watch</span> · {want.join(", ")}</div>}
    </div>
  );
  return (
    <Tooltip title={title} placement="topLeft" trigger={["hover", "click"]}>
      <span className={`who-marks${large ? " who-marks--large" : ""}`} aria-label={`${seen.length} seen, ${want.length} want to watch`}>
        {seen.length > 0 && <span className="who-marks-n who-marks-n--seen"><EyeFilled />{seen.length}</span>}
        {want.length > 0 && <span className="who-marks-n who-marks-n--want"><HeartFilled />{want.length}</span>}
      </span>
    </Tooltip>
  );
});

export default WhoMarks;
