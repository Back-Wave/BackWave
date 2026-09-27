# Workflows

A workflow is a graph of jobs with dependencies. The Workflows page lists every workflow with its derived status. The workflow page shows the member graph: each member with its queue and state, and a link to its full job page. For `order-fulfillment`, `validate-order` runs first, then `charge-payment` and `reserve-inventory` in parallel, then `pack-shipment`, then `order-notification`.

## Sub-features

- `workflows-list` lists the workflows with their name, status, and member count.
- `workflow-graph` shows the status, the name, the member count, and the `Member graph` of one workflow.
- `workflow-member` opens one member's panel with `Inspect <member>`, and its job page with `Open full job page`.
- `workflow-failed` shows a workflow where a member failed, and the members that never ran.

## How to get to it (user POV)

- Choose `Workflows` in the sidebar, then a workflow in the list.
- Open the `graph` link that `POST /workflows/order-fulfillment` returns.
- Choose `Open full job page` under a member to open that member's job page.

## Driving it with bwv

Preconditions:

- `bwv doctor` passes.
- The `critical`, `bulk`, and `low` queues are `Claiming`.
- No earlier recipe in this run used the names `wf`, `wf-fail`, or `wf-*`.

- **Start a workflow.** Start `order-fulfillment`. Run `bwv api POST '/workflows/order-fulfillment?orderRef=ORD-9&amount=12&itemCount=2' wf`. The response has `workflowId`, `"fail": false`, and `"graph": "/backwave/workflows/<id>"`.
- **Open the list.** Choose `Workflows`. Run `bwv browser open /backwave/workflows`, then `bwv shot wf-list`. The list has a row with `link "<first 8 chars>…"`, `order-fulfillment ORD-9`, `Succeeded`, and `5` members.
- **Open the graph.** Open the graph link. Run `bwv browser open /backwave/workflows/<workflowId>`, then `bwv shot wf-graph`. The page has `heading "Workflow <id>"`, `MEMBERS 5`, `Member graph`, `5 MEMBERS · 5 EDGES`, and the links `Inspect validate-order`, `Inspect charge-payment`, `Inspect reserve-inventory`, `Inspect pack-shipment`, and `Inspect order-notification`. Each member reads `<queue> · Succeeded` after about 2 s.
- **Inspect a member.** Choose `Inspect pack-shipment`. Run `bwv browser click @<uid>`, then `bwv shot wf-member`. The URL gets `?member=<jobId>`, and the page shows that member's detail.
- **Start a failing workflow.** Start the same workflow with a failure. Run `bwv api POST '/workflows/order-fulfillment?orderRef=ORD-10&amount=12&itemCount=2&fail=true' wf-fail`, then open its `graph` link and run `bwv shot wf-fail-graph`. The page shows `Failed` with "failure dominates". `charge-payment` is `critical · Dead-Lettered`. `pack-shipment` and `order-notification` are `Cancelled`. `validate-order` and `reserve-inventory` are `Succeeded`.
- **Proof.** Read the graph through the API. Run `bwv api GET /monitor/workflows/<workflowId> wf-monitor`. The response lists 5 members, the dependency edges, and the derived status. Run `bwv sql wf-rows "select workflow_id, name from backwave_workflows"` (`backwave.workflows` on Postgres). Each started workflow has one row.

## Gotchas

- The member order in the snapshot is the graph layout, not the run order. Read the state of each member, not its position.
- Workflows are a Pro feature. The Pro banner shows on every page. It does not block the feature.
- A member's job page has no link back to its workflow. Go back with `bwv browser open /backwave/workflows/<workflowId>`.
- A Cancelled member has `ATTEMPT 0` and `TERMINAL CAUSE parent-failure:DeadLettered`.
- A paused `critical` queue stalls `validate-order`, `charge-payment`, and `pack-shipment`. Then the workflow stays in progress.
- `order-notification` also makes the sample's Slack observer write a `slack-observer:` line to `app.log`.
- The sample has more workflow endpoints: `/workflows/fan-out-fan-in`, `/workflows/job-output`, `/workflows/checkout`, and `/workflows/escape-hatches/*`. This recipe covers only `order-fulfillment`.
