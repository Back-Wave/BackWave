# BackWave Pro Commercial License Agreement

**Version 1.0 — Effective July 1, 2026**

BY DOWNLOADING, INSTALLING, REFERENCING, OBTAINING A LICENSE KEY FOR, OR OTHERWISE USING THE
SOFTWARE, YOU — ON YOUR OWN BEHALF AND ON BEHALF OF THE ORGANIZATION FOR WHICH YOU ACT — AGREE THAT
YOU HAVE READ, UNDERSTOOD, AND AGREE TO BE BOUND BY THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
DOWNLOAD, INSTALL, REFERENCE, OBTAIN A LICENSE KEY FOR, OR USE THE SOFTWARE.

This BackWave Pro Commercial License Agreement (this "**Agreement**") is between DeVito Digital
Solutions LLC, a Delaware limited liability company ("**Licensor**"), and the individual or entity
exercising rights under it ("**You**"). If you use the Software on behalf of an organization,
"You" means that organization, and you represent and warrant that you have the authority to bind
that organization to this Agreement.

This Agreement governs **BackWave Pro** — the `BackWave.Pro`, `BackWave.Pro.Dashboard`, and
`BackWave.Pro.Mcp` NuGet packages (`BackWave.Pro.Dashboard` contributes Pro-only surfaces to the
free `BackWave.Dashboard`; the base dashboard itself is free), together with their associated
documentation and any updates Licensor makes available (collectively, the "**Software**"). The base `BackWave` packages — including
`BackWave.Dashboard` — are licensed separately and free of charge under the BackWave Free-Use
License Agreement; nothing in this Agreement changes Your rights to the base BackWave packages.

## 1. Definitions

- **"Application"** means a software application or service that You develop, own, or operate, into
  which the Software is incorporated as a component.
- **"Gross Annual Revenue"** means the total worldwide gross revenue of Your organization and its
  Affiliates for Your most recently completed fiscal year, from all sources. For a non-profit
  organization, it means the organization's total annual budget.
- **"Affiliate"** means any entity that controls, is controlled by, or is under common control with
  You, where "control" means ownership of more than fifty percent (50%) of the voting interests of
  an entity.
- **"License Key"** means the cryptographically signed license string, if any, that Licensor issues
  to You.
- **"Production Use"** means use of the Software to serve live workloads for Your business or Your
  customers, as opposed to development, testing, evaluation, staging, or continuous-integration
  use.
- **"Term"** has the meaning given in Section 10.

## 2. License Grant

Subject to Your compliance with this Agreement — including, where applicable, payment of the fees in
Section 5 — Licensor grants You a worldwide, non-exclusive, non-transferable, non-sublicensable
(except as expressly stated in this Section 2) license to:

1. install and use the Software for Your business purposes;
2. incorporate the Software as a component of Your Applications, deploy and run those Applications
   on infrastructure operated by or for You (including third-party cloud hosting You administer),
   and make those Applications available to Your customers and end users; and
3. distribute the Software solely as an embedded component of Your Applications, and permit end
   users of Your Applications to use the Software as part of those Applications, provided You make
   each such Application available under terms that protect the Software at least as much as this
   Agreement does (including prohibiting standalone use of the Software).

This license is **per organization and covers unlimited deployments** — there is no per-seat,
per-node, per-server, or per-deployment counting. It covers You **and Your Affiliates**: Your
Affiliates may use the Software under this Agreement as if they were You, and You are responsible
for their compliance. Gross Annual Revenue is aggregated across You and Your Affiliates for the same
reason — one group, one license, one revenue band.

**Contractors and agencies.** If a contractor or agency builds and deploys an Application
incorporating the Software for its client under the contractor's own license, that use is permitted
for as long as the contractor maintains the Application for the client. If the client assumes
ownership or maintenance of the Application, the client must obtain its own license appropriate for
its organization (including free use under Section 4 if the client qualifies).

## 3. Non-Production Use

You may install and use the Software free of charge, without a License Key and regardless of Your
Gross Annual Revenue, for development, testing, evaluation, staging, and continuous-integration
purposes; this includes modifying the Software's source for those non-production purposes. Sections 4
and 5 apply only to Production Use of the Software.

## 4. Free Production Use for Small Organizations (the Revenue Threshold)

**If Your Gross Annual Revenue is less than one million U.S. dollars (US $1,000,000), You may use
the Software free of charge** under this Agreement, with the same features and the same rights as a
paying licensee. This is an honor-system community grant: it is **self-assessed**, requires no
approval, no application, and no License Key, and Licensor does not measure Your revenue.

If Your Gross Annual Revenue is one million U.S. dollars (US $1,000,000) or more, a paid license is
required for Production Use of the Software, and You agree to obtain one and pay the applicable
fees. Your eligibility for free use is assessed as of the start of each of Your fiscal years, based
on Your most recently completed fiscal year. If You exceed the threshold, You may continue free use
through the end of the fiscal year in which the threshold was first exceeded; a paid subscription is
required from the start of Your following fiscal year.

The Software's features and behavior are **identical** whether You use it free of charge or under a
paid license. A license — and the revenue band recorded on it — affects **price only**,
never which features exist or how they behave.

## 5. Fees and Bands

- **Revenue-banded pricing.** For organizations that require a paid license, pricing is banded by
  Gross Annual Revenue. A higher band changes **price only**; every band receives identical
  Software features. Revenue is **self-reported** by You at the time of purchase; the Software does
  not detect or report it. Section 6 governs the accuracy of what You report.
- **Price lock.** The price for Your band is **locked while You remain continuously subscribed** — a
  published price increase does not affect You until You renew. Your applicable band is re-assessed
  **only at renewal**: if Your Gross Annual Revenue has moved You into a different band, the new
  band's price applies from the start of Your next Term.
- **Payment.** Fees are stated in the applicable order or on Licensor's pricing page, are payable in
  U.S. dollars, and are non-refundable except as expressly stated in an order. You are responsible
  for all taxes other than taxes on Licensor's income.
- **No support.** No license, paid or free, includes support or maintenance; the Software is provided
  strictly as-is. Licensor is under no obligation to provide updates, fixes, or assistance.

## 6. Verification

- **Accuracy.** Licensor relies on the revenue and eligibility information You provide. You
  represent and warrant that any Gross Annual Revenue figure or revenue band You report — at
  purchase or in claiming free use under Section 4 — is accurate.
- **Records.** Licensor may, no more than once per calendar year, submit a written request to You
  for records reasonably necessary to demonstrate Your compliance with this Agreement, including
  Your eligibility for free use under Section 4 and the revenue band applicable to Your license.
  You will provide such records within thirty (30) days of receipt of the written request. Each
  party will bear its own costs associated with any such verification, unless a material breach is
  discovered, in which case You will reimburse Licensor's reasonable verification costs.
- **True-up.** If You engaged in Production Use of the Software without a required paid
  subscription, or paid at a lower band than Your Gross Annual Revenue required, the fees You would
  have paid at the correct band for the period of the shortfall become immediately due.

## 7. License Keys and Enforcement (Soft-Fail; No Phone-Home)

The Software's license enforcement is designed to **never interfere with Your operations**. You
acknowledge and agree that:

1. **Offline validation, no telemetry.** When supplied, a License Key is verified entirely offline,
   against a public key embedded in the Software. The Software makes **no network call** to validate
   a License Key and sends **no license or usage telemetry** to Licensor, at startup or at any other
   time.
2. **Always soft-fail.** A missing, malformed, expired, or otherwise unrecognized License Key
   **never disables any feature, never degrades behavior, and never stops the Software from running**.
   When a License Key's subscription term has ended (see Section 10), the Software continues to run in
   full and shows the unlicensed notice — the only effect is informational: a one-time startup log
   warning and a notice ("banner") in the BackWave dashboard indicating that BackWave Pro is
   unlicensed. Every feature continues to work in full; the notice is a reminder to renew, not a
   restriction.
3. **Honor system.** Because enforcement is informational only, an unlicensed process is technically
   indistinguishable between a compliant free user under Section 4 and an organization that owes a
   fee. Your obligation to hold a current paid subscription when required by Section 4 is a
   **contractual** obligation that does not depend on any technical control, and running the Software
   without a required current subscription is a breach of this Agreement even though the Software will
   continue to run.

## 8. Restrictions

Except as expressly permitted in Section 2, You shall not, and shall not permit any third party to:

1. distribute, resell, rent, lease, lend, sublicense, or otherwise make the Software available to
   any third party **except** as a non-primary, embedded component of Your Applications — that is,
   You may ship the Software inside an Application whose primary value is Your own functionality, but
   You may not redistribute the Software on a standalone basis, as a software development kit, or as
   a hosting, managed-service, or platform offering whose primary value is the Software itself. Your
   customers and other third parties who use the Software other than as end users of Your
   Applications must obtain their own license;
2. modify or create derivative works of the Software for Production Use, other than through the
   extension points the Software exposes for that purpose (modification for the non-production
   purposes described in Section 3 is permitted);
3. remove, obscure, or alter any copyright, trademark, or other proprietary notice in or on the
   Software, or circumvent, disable, or tamper with the License Key verification described in
   Section 7;
4. publish, disclose, or share a License Key outside You and Your Affiliates, or use a License Key
   issued to a different organization — You will treat License Keys as Licensor's confidential
   information; or
5. use the Software to build a product or service that competes with the Software — meaning one
   offered to third parties whose primary value is the background-job processing, scheduling, or
   workflow-orchestration functionality the Software provides. Using the Software within Your
   Applications is not competing merely because those Applications run background jobs.

## 9. Ownership

The Software is licensed, not sold. Licensor and its licensors retain all right, title, and interest
in and to the Software, including all intellectual property rights. No rights are granted to You
other than as expressly set out in this Agreement. Although the Software's source code is publicly
available, the Software is licensed - not placed in the public domain or made open source - and Your
rights in it are only those expressly granted here.

## 10. Term: Subscription

1. **A paid license is a subscription.** Unless an order states otherwise, You purchase the right to
   use the Software for a fixed **annual** subscription term, as stated in the order (the
   "**Term**"), which renews as stated in the order. Your license to use the Software is valid **only
   during an active subscription Term**.
2. **No perpetual or fallback license.** When Your subscription Term ends without renewal, Your
   license terminates and You must **stop using the Software** — there is **no perpetual license and
   no right to continue using any prior version** of the Software after Your subscription ends.
   Renewing Your subscription restores Your license.
3. **Soft expiry (consistent with Section 7).** The Software does not enforce expiry by stopping.
   When Your subscription Term ends, the Software continues to run in full and shows only the
   unlicensed notice; it is **never a kill-switch**, so a running deployment never fails because a
   subscription lapsed. Continuing to use the Software after Your subscription Term ends is
   nonetheless a breach of this Agreement, and You agree to renew or stop.
4. **Free use.** For an organization using the Software free of charge under Section 3 or Section 4,
   this Agreement continues for as long as it remains eligible and complies with this Agreement; free
   use is not a subscription and does not expire while You remain eligible.

## 11. Termination

Either party may terminate this Agreement if the other party materially breaches it and fails to
cure the breach within thirty (30) days after written notice. On termination, You must stop using
the Software and destroy all copies in Your possession or control. Termination does not entitle You
to any refund. Sections 8, 9, and 12 through 17, together with any accrued payment obligations
(including amounts due under Section 6), survive termination.

## 12. Indemnification

You will defend, indemnify, and hold harmless Licensor from and against any third-party claim, and
the resulting damages, costs, and reasonable attorneys' fees, arising out of (a) Your Applications,
or (b) Your use of the Software in violation of this Agreement.

## 13. Feedback

If You provide Licensor with suggestions, bug reports, or other feedback about the Software
("**Feedback**"), Licensor may use that Feedback for any purpose without restriction or obligation to
You, and You grant Licensor a perpetual, irrevocable, worldwide, royalty-free license to do so.

## 14. Warranty Disclaimer

THE SOFTWARE AND ANY SUPPORT ARE PROVIDED "AS IS" AND "AS AVAILABLE," WITHOUT WARRANTY OF ANY KIND.
TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, LICENSOR DISCLAIMS ALL WARRANTIES AND CONDITIONS,
WHETHER EXPRESS, IMPLIED, STATUTORY, OR OTHERWISE, INCLUDING ANY IMPLIED WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, TITLE, AND NON-INFRINGEMENT. LICENSOR DOES NOT
WARRANT THAT THE SOFTWARE WILL BE UNINTERRUPTED, ERROR-FREE, OR SECURE.

## 15. Limitation of Liability

TO THE MAXIMUM EXTENT PERMITTED BY APPLICABLE LAW, LICENSOR WILL NOT BE LIABLE FOR ANY INDIRECT,
INCIDENTAL, SPECIAL, CONSEQUENTIAL, OR PUNITIVE DAMAGES, OR FOR ANY LOSS OF PROFITS, REVENUE, DATA,
OR GOODWILL, ARISING OUT OF OR RELATING TO THIS AGREEMENT OR THE SOFTWARE, EVEN IF ADVISED OF THE
POSSIBILITY OF SUCH DAMAGES. LICENSOR'S TOTAL AGGREGATE LIABILITY ARISING OUT OF OR RELATING TO THIS
AGREEMENT AND THE SOFTWARE WILL NOT EXCEED THE GREATER OF (A) THE FEES YOU PAID FOR THE SOFTWARE IN
THE TWELVE (12) MONTHS PRECEDING THE EVENT GIVING RISE TO THE CLAIM, OR (B) ONE HUNDRED U.S. DOLLARS
(US $100). SOME JURISDICTIONS DO NOT ALLOW CERTAIN OF THESE LIMITATIONS, SO THEY MAY NOT APPLY TO
YOU, IN WHICH CASE THEY APPLY TO THE MAXIMUM EXTENT PERMITTED BY LAW.

## 16. High-Risk Activities

The Software is not fault-tolerant and is not designed for use in any environment requiring fail-safe
performance in which the failure of the Software could lead to death, personal injury, or severe
physical or environmental damage. You are solely responsible for any such use.

## 17. General

- **Governing law and venue.** This Agreement is governed by the laws of the State of **Delaware**,
  USA, excluding its conflict-of-laws rules, and excluding the United Nations Convention on Contracts
  for the International Sale of Goods. All judicial proceedings arising out of or relating to this
  Agreement shall be initiated in the state or federal courts sitting in Delaware, and each party
  irrevocably submits to the jurisdiction and venue of those courts. EACH PARTY WAIVES, TO THE
  FULLEST EXTENT PERMITTED BY APPLICABLE LAW, ANY RIGHT IT MAY HAVE TO TRIAL BY JURY IN ANY LEGAL
  PROCEEDING DIRECTLY OR INDIRECTLY ARISING OUT OF OR RELATING TO THIS AGREEMENT.
- **Export compliance.** You will comply with all applicable export and sanctions laws in Your use of
  the Software.
- **Assignment.** You may not assign this Agreement without Licensor's prior written consent, except
  to a successor of all or substantially all of Your business that is not a competitor of Licensor.
  Licensor may assign this Agreement freely.
- **Notices.** Legal notices to Licensor must be in writing and sent by email to
  team@backwave.app. Legal notices to You may be sent to the email address associated with Your
  order or, for free use, to any contact address You have provided to Licensor.
- **Waiver.** A party's failure to enforce any provision of this Agreement is not a waiver of its
  right to enforce that provision, or any other, later.
- **Severability.** If any provision of this Agreement is held unenforceable, the remaining provisions
  remain in full force, and the unenforceable provision will be enforced to the maximum extent
  permitted by law.
- **Entire agreement.** This Agreement, together with any order referencing it, is the entire
  agreement between You and Licensor regarding the Software and supersedes all prior or
  contemporaneous understandings on that subject. If an order conflicts with this Agreement, the
  order controls for that purchase. Terms in any purchase order or similar document You issue are of
  no effect.
- **Changes.** Licensor may update this Agreement for future versions of the Software. The version of
  this Agreement distributed with a given release of the Software governs Your use of that release.
  A change never reduces Your rights during an already-paid subscription Term: material changes apply
  only to new subscriptions, and to renewals that begin after the change takes effect.

Licensing and purchasing: **team@backwave.app** · https://backwave.app
