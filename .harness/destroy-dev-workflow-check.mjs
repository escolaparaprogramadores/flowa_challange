// Regression check of .github/workflows/9-destroy-dev.yml, run from the repository root.
// It holds the destroy to the deploy (same pins, Terraform, state key and queue), keeps it manual and guarded,
// keeps every terraform call on a closed reviewed list, passes every image tag to the plan (a null one breaks
// the refresh) and runs both terraform steps for real with fakes, so that a second run on an empty state, a
// leftover resource, a failed read of the state or an ARN in an error are all caught.
import { mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const destroyWorkflowPath = '.github/workflows/9-destroy-dev.yml';
const deployWorkflowPath = '.github/workflows/2-develop.yml';
// The checkout on Windows turns the line ends into CRLF.
const destroyWorkflow = readFileSync(destroyWorkflowPath, 'utf8').replace(/\r\n/g, '\n');
const deployWorkflow = readFileSync(deployWorkflowPath, 'utf8').replace(/\r\n/g, '\n');
const failures = [];

function requireInDestroyWorkflow(expectedText, reason) {
  if (!destroyWorkflow.includes(expectedText)) failures.push(`${destroyWorkflowPath}: missing ${expectedText} (${reason})`);
}

// Comments are not executed, so they are left out of every check on the shell code.
function shellScriptOfStep(stepName) {
  const stepBlock = destroyWorkflow.match(new RegExp(`- name: ${stepName}\\n((?: {8}.*\\n|\\n)*)`))?.[1];
  const shellLines = stepBlock?.match(/ {8}run: \|\n((?: {10}.*\n|\n)*)/)?.[1];
  if (!shellLines) failures.push(`${destroyWorkflowPath}: no step "${stepName}" with a run block`);
  return (shellLines ?? '').replace(/^ {10}/gm, '').split('\n').filter((shellLine) => !/^\s*#/.test(shellLine)).join('\n');
}

const destroyTriggerBlock = destroyWorkflow.match(/^on:\n((?:[ #].*\n|\n)*)/m)?.[1] ?? '';
const destroyTriggerNames = [...destroyTriggerBlock.matchAll(/^ {2}([a-z_]+):/gm)].map((triggerMatch) => triggerMatch[1]);
if (destroyTriggerNames.join(',') !== 'workflow_dispatch') {
  failures.push(`${destroyWorkflowPath}: triggers are [${destroyTriggerNames.join(', ')}], expected only workflow_dispatch`);
}
const destroyJobNames = [...(destroyWorkflow.match(/^jobs:\n((?:[ #].*\n|\n)*)/m)?.[1] ?? '').matchAll(/^ {2}([A-Za-z0-9_-]+):/gm)]
  .map((jobMatch) => jobMatch[1]);
if (destroyJobNames.join(',') !== 'destroy') {
  failures.push(`${destroyWorkflowPath}: jobs are [${destroyJobNames.join(', ')}], expected only "destroy", so that the guard covers all of them`);
}
if (!/^ {4}if: github\.ref == 'refs\/heads\/develop' && inputs\.confirmation == 'destruir'$/m.test(destroyWorkflow)) {
  failures.push(`${destroyWorkflowPath}: the destroy job has no guard "if: github.ref == 'refs/heads/develop' && inputs.confirmation == 'destruir'"`);
}
requireInDestroyWorkflow('    concurrency:\n      group: deploy-dev\n      cancel-in-progress: false\n', 'same queue as the deploy, never cancelled');
if (!deployWorkflow.includes('      group: deploy-dev\n')) failures.push(`${deployWorkflowPath}: the deploy queue is no longer deploy-dev`);
requireInDestroyWorkflow('          terraform_wrapper: false\n', 'the wrapper would echo the whole output in the public log');
requireInDestroyWorkflow('          mask-aws-account-id: true\n', 'the account id must not show in the public log');
requireInDestroyWorkflow('    defaults:\n      run:\n        shell: bash\n', 'pipefail in every step');

const destroyActionPins = [...destroyWorkflow.matchAll(/uses: (\S+@\S+)/g)].map((pinMatch) => pinMatch[1]);
if (destroyActionPins.length === 0) failures.push(`${destroyWorkflowPath}: no pinned action found`);
for (const destroyActionPin of destroyActionPins) {
  if (!deployWorkflow.includes(`uses: ${destroyActionPin}`)) failures.push(`${destroyWorkflowPath}: ${destroyActionPin} differs from the deploy`);
}

const terraformVersionOf = (workflowText) => workflowText.match(/terraform_version: (\S+)/)?.[1];
if (!terraformVersionOf(destroyWorkflow) || terraformVersionOf(destroyWorkflow) !== terraformVersionOf(deployWorkflow)) {
  failures.push(`terraform_version ${terraformVersionOf(destroyWorkflow)} in the destroy, ${terraformVersionOf(deployWorkflow)} in the deploy`);
}

const backendConfigsOf = (workflowText) => [...workflowText.matchAll(/-backend-config="[^"]+"/g)].map((configMatch) => configMatch[0]);
const destroyBackendConfigs = backendConfigsOf(destroyWorkflow);
const deployBackendConfigs = backendConfigsOf(deployWorkflow);
if (destroyBackendConfigs.length === 0 || destroyBackendConfigs.join(' ') !== deployBackendConfigs.join(' ')) {
  failures.push(`state backend differs: destroy [${destroyBackendConfigs.join(' ')}], deploy [${deployBackendConfigs.join(' ')}]`);
}

// Closed list: every shell line that names the terraform binary (word "terraform" not glued to _ - or .),
// however it is written (path, quotes, -chdir, after ; or &&). A new one fails until it is reviewed here:
// the raw call inside the helper, the state read whose error goes through the ARN filter, and two echo texts.
const reviewedTerraformLines = [
  'if ! terraform "$terraform_subcommand" -no-color "$@" > "$TERRAFORM_LOG" 2>&1; then',
  'echo "terraform $terraform_subcommand failed; the full output stays only on the runner" >&2',
  'echo "terraform init ok"',
  'if ! state_resources=$(terraform state list 2> "$state_list_errors"); then',
];
const allShellLines = [...destroyWorkflow.matchAll(/ {8}run: \|\n((?: {10}.*\n|\n)*)/g)]
  .flatMap((runBlockMatch) => runBlockMatch[1].split('\n'))
  .map((shellLine) => shellLine.trim())
  .filter((shellLine) => shellLine && !shellLine.startsWith('#'));
const terraformLines = allShellLines.filter((shellLine) => /(?<![\w-])terraform(?![\w.-])/.test(shellLine));
for (const terraformLine of terraformLines) {
  if (!reviewedTerraformLines.includes(terraformLine)) {
    failures.push(`${destroyWorkflowPath}: "${terraformLine}" calls terraform outside the reviewed list (it would bypass the quiet helper)`);
  }
}
for (const reviewedTerraformLine of reviewedTerraformLines) {
  if (!terraformLines.includes(reviewedTerraformLine)) failures.push(`${destroyWorkflowPath}: missing ${reviewedTerraformLine}`);
}

const declaredImageTagVariables = readdirSync('infra-aws')
  .filter((terraformFileName) => terraformFileName.endsWith('.tf'))
  .flatMap((terraformFileName) => {
    const terraformSource = readFileSync(join('infra-aws', terraformFileName), 'utf8');
    return [...terraformSource.matchAll(/^variable "([a-z_]+_image_tag)"/gm)].map((variableMatch) => variableMatch[1]);
  });
if (declaredImageTagVariables.length === 0) {
  failures.push('no *_image_tag variable declared in infra-aws/*.tf: the check would pass empty');
}

function runShellStep(stepScript, shellPrelude) {
  const stepScriptFolder = mkdtempSync(join(tmpdir(), 'destroy-dev-step-'));
  try {
    const runnerTempFolder = stepScriptFolder.replace(/\\/g, '/');
    const stepScriptPath = join(stepScriptFolder, 'step.sh');
    writeFileSync(stepScriptPath, `RUNNER_TEMP='${runnerTempFolder}'\nGITHUB_SHA=${'a'.repeat(40)}\n${shellPrelude(runnerTempFolder)}\n${stepScript}`);
    // Same flags as the shell: bash of the job on the runner.
    const stepRun = spawnSync('bash', ['--noprofile', '--norc', '-eo', 'pipefail', stepScriptPath.replace(/\\/g, '/')], { encoding: 'utf8' });
    const recordedCalls = (() => {
      try { return readFileSync(join(stepScriptFolder, 'calls.txt'), 'utf8').trim().split('\n'); } catch { return []; }
    })();
    return { stepRun, stepOutput: `${stepRun.stdout ?? ''}${stepRun.stderr ?? ''}`, recordedCalls };
  } finally {
    rmSync(stepScriptFolder, { recursive: true, force: true });
  }
}

// The plan step sources the helper from RUNNER_TEMP; a fake one there records the calls and writes the log.
const destroyPlanAndApplyScript = shellScriptOfStep('terraform plan -destroy and apply');
const expectedPlanCall = ['plan -destroy -out=tfplan', ...declaredImageTagVariables.map((imageTagVariable) => `-var ${imageTagVariable}=${'a'.repeat(40)}`)].join(' ');
const terraformPlanLogByCase = {
  'plan with a resource to destroy': '  # aws_db_instance.banco will be destroyed\\nPlan: 0 to add, 0 to change, 1 to destroy.',
  'empty state (second run)': 'No changes. No objects need to be destroyed.',
};
for (const [planCaseName, terraformPlanLog] of Object.entries(terraformPlanLogByCase)) {
  const { stepRun, stepOutput, recordedCalls } = runShellStep(destroyPlanAndApplyScript, (runnerTempFolder) => `cat > "${runnerTempFolder}/terraform-quietly.sh" <<'END'
run_terraform_quietly() {
  echo "$*" >> "$RUNNER_TEMP/calls.txt"
  TERRAFORM_LOG="$RUNNER_TEMP/terraform-$1.log"
  if [ "$1" = plan ]; then printf '${terraformPlanLog}\\n' > "$TERRAFORM_LOG"; else echo 'Apply complete! Resources: 0 added, 0 changed, 1 destroyed.' > "$TERRAFORM_LOG"; fi
}
END`);
  if (stepRun.error) {
    failures.push(`could not run bash for the plan step: ${stepRun.error.message}`);
    continue;
  }
  if (stepRun.status !== 0) failures.push(`plan step with ${planCaseName}: exit ${stepRun.status}\n${stepOutput}`);
  if (recordedCalls.join(' | ') !== `${expectedPlanCall} | apply tfplan`) {
    failures.push(`plan step with ${planCaseName}: calls were [${recordedCalls.join(' | ')}], expected [${expectedPlanCall} | apply tfplan]`);
  }
}

const emptyStateStepScript = shellScriptOfStep('Nothing left in the state');
const fakeTerraformByStateCase = {
  'empty state': { fakeTerraform: 'terraform() { return 0; }', expectedSuccess: true },
  'only data sources left': { fakeTerraform: "terraform() { echo 'data.aws_caller_identity.conta'; }", expectedSuccess: true },
  'a resource left': { fakeTerraform: "terraform() { printf 'data.aws_region.atual\\naws_db_instance.banco\\n'; }", expectedSuccess: false },
  'state read fails': {
    fakeTerraform: "terraform() { echo 'Error: AccessDenied for arn:aws:iam::123456789012:role/flowa-gha' >&2; return 1; }",
    expectedSuccess: false,
    forbiddenOutput: 'arn:aws',
    expectedOutput: '<hidden arn>',
  },
};
for (const [stateCaseName, { fakeTerraform, expectedSuccess, forbiddenOutput, expectedOutput }] of Object.entries(fakeTerraformByStateCase)) {
  const { stepRun, stepOutput } = runShellStep(emptyStateStepScript, () => fakeTerraform);
  if (stepRun.error) {
    failures.push(`could not run bash for the case "${stateCaseName}": ${stepRun.error.message}`);
    continue;
  }
  if ((stepRun.status === 0) !== expectedSuccess) {
    failures.push(`last step with ${stateCaseName}: exit ${stepRun.status}, expected ${expectedSuccess ? 'success' : 'failure'}`);
  }
  if (forbiddenOutput && stepOutput.includes(forbiddenOutput)) failures.push(`last step with ${stateCaseName}: the log shows "${forbiddenOutput}"`);
  if (expectedOutput && !stepOutput.includes(expectedOutput)) failures.push(`last step with ${stateCaseName}: the log does not show "${expectedOutput}"`);
}

if (failures.length > 0) {
  console.error(failures.join('\n'));
  process.exit(1);
}
console.log(
  `ok: manual and guarded, same queue, pins (${destroyActionPins.length}), Terraform and state as the deploy; ` +
  `${terraformLines.length} terraform lines, all reviewed; ` +
  `plan step calls plan -destroy with ${declaredImageTagVariables.length} image tags then apply tfplan, also on an empty state; ` +
  'last step: empty and data-only pass, leftover and failed read fail, ARN hidden',
);
