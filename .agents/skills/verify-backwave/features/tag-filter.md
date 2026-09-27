# Tag filter

A job carries Tags: Labels (a single word, for example `billing`) and key-value tags (for example `tenant:acme`). The Jobs list shows the tags as pills. An operator chooses a pill or a "Top labels" entry to filter the list, and can combine filters. The `Filter by Tag` box suggests Labels and keys as the operator types.

## Sub-features

- `tags-pills` shows each job's tags as links in the `TAGS` column.
- `tags-filter-label` filters the list by one Label (`?tl=<label>`).
- `tags-filter-key-value` filters the list by one key-value tag (`?tk=<key>&tv=<value>`).
- `tags-combined` adds a second tag to the current filter.
- `tags-top-labels` lists the most frequent Labels within the current filter, with counts.
- `tags-suggest` suggests Labels and keys in the `Filter by Tag` combobox.

## How to get to it (user POV)

- Choose `Jobs` in the sidebar. The `Filter by Tag` box and the "Top labels" list are under the filter form.
- Choose a tag pill in a job row. The pill adds its tag to the current filter.
- Type in the `Filter by Tag` combobox and choose a suggestion.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- No earlier recipe in this run used the names `tagged`, `plain`, or `tags-*`.

- **Create tagged jobs.** Enqueue one tagged report and one plain greeting. Run `bwv api POST '/jobs/tagged-report?tenant=acme&amount=12&priority=true' tagged`, then `bwv api POST '/jobs/enqueue?name=Plain' plain`. The first response lists `"tags": ["tenant=acme", "priority"]`.
- **See the pills.** Choose `Jobs`. Run `bwv browser open /backwave/jobs`, then `bwv shot tags-list`. The `tagged-report` row has the links `billing`, `priority`, `processed`, `report`, `amount-band:low`, and `tenant:acme`, each with the description "Filter by this Tag".
- **Filter by key-value.** Choose the `tenant:acme` pill. Run `bwv browser open '/backwave/jobs?tk=tenant&tv=acme'`, then `bwv shot tags-tenant`. Only the `tagged-report` row shows. "Top labels" lists `billing 1`, `priority 1`, `processed 1`, and `report 1`.
- **Combine a Label.** Choose `billing 1` in "Top labels". Run `bwv browser click @<uid of link "billing 1">`, then `bwv shot tags-combined`. The URL is `/backwave/jobs?tk=tenant&tv=acme&tl=billing`, and the `tagged-report` row still shows.
- **Use the suggestions.** Type in `Filter by Tag`. Click the `combobox "Filter by Tag"` uid, then run `bwv browser type ten` and `bwv shot tags-suggest`. The `listbox "Tag suggestions"` has `option "tenant ›"`. Click that option. The listbox now has `option "acme"`. Click it. The URL is `/backwave/jobs?tk=tenant&tv=acme`.
- **Filter by a Label only.** Run `bwv browser open '/backwave/jobs?tl=priority'`. Only the `tagged-report` row shows. The `greet` row for `Plain` does not show.
- **Proof.** Read the store and the API. Run `bwv sql tags-rows "select job_id, key, value from backwave_job_tags"` (`backwave.job_tags` on Postgres). The tagged job has 6 rows. A Label has an empty `key`. Run `bwv api GET '/monitor/tagged?tenant=acme' tags-api`. The response has only the tagged job.

## Gotchas

- `/jobs/tagged-report` needs `priority=true`. `priority=high` returns 400.
- The tags come from three sources: the job type (`billing`, `report`), the enqueue call (`tenant=acme`, `priority`), and the handler at run time (`processed`, `amount-band`). The run-time tags show only after the job ran.
- `amount=12` gives `amount-band:low`. Assert the band that the amount gives, not a fixed value.
- A key suggestion (`tenant ›`) does not filter. It opens the values of that key. The link `tenant ›` in the listbox goes back to all tags.
- A pill for a tag that is already in the filter adds the same `tk`/`tv` pair again. The result is the same list.
