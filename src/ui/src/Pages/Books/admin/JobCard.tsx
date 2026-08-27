/**
 * Books' binding of the site's job card (R9 S6: `src/ui/src/admin/JobCard`). All this file adds is
 * the host's job endpoints; the card itself is the shared one every section's admin uses.
 */
import SharedJobCard, { JobProgress, JobStateTag, type JobCardProps as SharedProps } from "../../../admin/JobCard";
import { booksJobApi } from "./adminApi";

export { JobProgress, JobStateTag };
export type JobCardProps = Omit<SharedProps, "api">;

export default function JobCard(props: JobCardProps) {
  return <SharedJobCard {...props} api={booksJobApi} />;
}
