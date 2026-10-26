# Repository Agent Workflow

This file defines the collaboration rules for this public repository. Explicit
user instructions take precedence. Keep these rules self-contained and suitable
for publication; do not include private paths, credentials, internal source
identities, or confidential planning information.

## Repository and branches

- The canonical branch is `main`. Use short-lived task branches based on the
  current `main`; a permanent `dev` branch is not part of the intended workflow.
- `publication/next` is the integration branch for the upcoming source replacement.
  Keep that work on this branch until its reviewed integration into `main`.
- Check the current branch, remotes, and tracked/untracked changes before
  substantial work. Fetch when remote freshness matters. Confirm the actual
  destination before any push; `origin` is expected to be the public GitHub remote.
- Preserve unrelated user changes. Never discard, clean, stash, or overwrite
  them without authorization covering those changes.
- Ignored content can contain local task records and handoff notes. Never use
  blanket cleanup of ignored files (`git clean -x` or `-X`); inspect and scope
  any authorized cleanup to named build/output paths, preserving `.local-work/`.
- Obtain explicit approval for commits, pushes, merges, branch deletion, history
  changes, tags, and releases/uploads unless the user has already authorized the
  specific action. Never force-push or rewrite shared history by default.
- Before committing, inspect status and the complete intended diff, including
  newly added files. Keep commits focused and messages concrete.

## TODO workflow

- Use `.local-work/` as the local task store, excluded through `.git/info/exclude`.
  On a fresh checkout, establish that exclusion before creating local task files.
  Do not create a remote tracker or publish local task content without approval.
- Read `.local-work/README.md` for the task index and current focus. Create TODOs
  from `.local-work/TEMPLATE.md` under `.local-work/tasks/` using stable filenames
  such as `0001-source-integration.md`. Allocate the next unused number; never
  renumber or move tasks when their status changes. Keep completed tasks in place.
- The TODO file owns its detailed status, work packages, decisions, and results.
  Keep its summary row in the index consistent in the same update. The index is
  a navigation aid, not a second detailed work log.
- If the local store is absent, recreate the index and template using this workflow
  when authorized, or report the missing context. Never infer past task completion.
- GitHub issues in this public repository must be treated as public content.
  Do not post internal notes there or assume a private project board hides them.
- Before a non-trivial task, check the selected task store for an existing TODO.
  If none exists, propose a TODO or create one when already authorized. Small,
  direct requests do not require a separate tracking item.
- Each TODO describes the objective, scope, deliverables, acceptance criteria,
  verification expectations, and relevant decisions or constraints.
- Split larger TODOs into meaningful, content-specific work packages. Use project
  concerns such as source import, packaging, CLI, WPF, access profiles, licensing,
  documentation, or runtime validation rather than a fixed generic step pattern.
- If scope or acceptance criteria are unclear, make the first work package an
  explicit refinement step and resolve them before dependent implementation.
- Process work packages one at a time. The user selects the next package; do not
  start another unless requested or already included in the authorized scope.
  Complete the selected package, including its checks and documentation.
- Keep tracking flat by default. Create sub-issues or split-out tasks only when
  requested or agreed because the existing item has become unmanageable.
- Use open, in-progress, blocked, done, and cancelled states. Record concrete
  blockers and outstanding decisions; do not mark incomplete work as done.
- On completing a package, update its tracking record with changes, verification
  results, limitations, remaining work, and the next proposed step. Close a TODO
  only when its agreed deliverables and acceptance criteria are satisfied.
- A TODO or work-package approval does not implicitly authorize Git publication,
  architectural changes, or access to a PLC target.

## Source integration and publication

- Treat source replacement as a deliberate snapshot integration. Obtain the
  incoming directory and its reported source identity from the user; do not
  invent inputs, revisions, versions, artifacts, or verification results.
- Before replacement, present a concrete import/removal/retention plan and obtain
  approval. Identify obsolete files explicitly and preserve repository-owned
  documentation, packaging, GitHub configuration, and collaboration rules.
- Preserve the root README's public-owned `Support this project` section immediately
  before `License` during source imports. Keep its shared wording and
  `https://github.com/sponsors/Lorenz-Software` target aligned with the companion
  PLC testing projects. Funding configuration is maintained separately in the
  organization's `.github` repository; do not introduce a repository-local
  funding file as part of source imports.
- Preserve the public `CONTRIBUTING.md` and `.github/ISSUE_TEMPLATE/` during
  source imports. Keep their contribution guidance and issue-form structure
  aligned with the companion PLC testing projects, while retaining this
  application's own links, environment fields, and verification instructions.
- Repository-wide scripts belong under `tools/`; component-owned scripts remain
  with their components. Preserve this public layout during later imports: map
  incoming root scripts to their tools counterparts and reconcile public changes
  instead of reintroducing duplicate root entry points. Update callers and owning
  documentation when script locations change.
- Agree the Git LFS representation before staging imported binary content.
  Preserve required contracts and resources; verify that LFS payloads resolve.
- Keep local build, package, and installer workflows independent of private
  services. Do not publish authentication helpers, internal planning material,
  temporary exports, session notes, or machine-specific configuration.
- Generic integration examples for users, including GitLab CI starters, are
  public product content. Distinguish them from private development infrastructure
  and registry/authentication helpers; do not remove CI support based on a vendor
  or platform name alone. Preserve these examples in later source selections.
- Scan the resulting tree for private information, obsolete infrastructure
  references, broken links, and accidentally retained legacy files.
- Agree the release version separately. Establish correspondence between the
  exact public source revision and every published binary. Rebuild or resolve
  mismatches after public-side changes before presenting artifacts as matching.
- Do not expand an application release to include separate PLC frameworks.

## Design and implementation

- Align product behavior, public/shared interfaces, communication contracts,
  access-profile boundaries, architecture, result semantics, lifecycle behavior,
  and persistence formats with the user before changing their design.
- Raise observed design problems with concrete options rather than silently
  redesigning the product during integration or maintenance.
- Prefer small coherent changes. Follow the owning component's style and avoid
  unrelated refactoring. Inspect actual project files and documentation before
  assuming a layout, target framework, tool, or script exists.
- Preserve application/shared-code boundaries. Vendor engineering and runtime
  dependencies belong inside their owning access profile, binding, or worker.
- Preserve declared modern .NET and .NET Framework targets unless migration is
  approved. Use committed binary contracts where required; do not replace them
  opportunistically or turn those boundaries into project references.
- Keep CLI automation and WPF manual/diagnostic operation consistent with their
  documented capabilities. Do not imply complete feature parity.
- Keep raw PLC access, event reconstruction, result aggregation, and JUnit output
  as separate responsibilities. PLC-side orchestration is outside application scope.

## Verification and runtime access

- Run checks appropriate to the change using the owning component's documented
  commands. For documentation-only work, review the text and run `git diff --check`.
- For code changes, build affected projects and run relevant available tests.
  Add focused tests when behavior and risk justify them, not merely to mirror code.
- Review dependency licenses and redistribution/NOTICE obligations before adding
  or updating production dependencies. Update the owning inventory and notices;
  run existing notice checks and affected package checks when applicable.
- Verify package and installer operation when changing those workflows. State
  unavailable prerequisites and checks that were not performed.
- Unit tests and Fake-profile runs do not establish live vendor-runtime
  qualification. Report actual checks and their limits accurately.
- Runtime-changing tests require explicit user approval and a specific disposable
  target. Never download to or restart a PLC, activate configurations, create a
  boot project, or disturb an existing engineering session without that approval.

## Documentation, communication, and handoff

- Use English for repository artifacts, code comments, application text, commit
  messages, and task records unless explicitly requested otherwise. Conversation
  with the user may be in German.
- Keep temporary sequencing, execution notes, and task-specific decisions in the
  selected task store. Record durable cross-task and design decisions in the
  owning repository documentation, independently of issue IDs or private links.
- Keep documents focused on their purpose. Global documentation describes stable
  responsibilities and models; component documentation owns component details.
- Keep local task records and temporary analysis artifacts untracked. Establish
  exclusion rules before creating them; never stage them as part of a broad add.
- At session end, record the current branch/task, last completed package, checks,
  open decisions, and next step in the selected store, or provide this handoff in
  the conversation if no persistent store has been chosen. Do not assume session
  scripts or additional agent skills exist.
- For analysis-only requests, do not modify files. For implementation requests,
  complete the authorized work and report the outcome, checks, and limitations.
