import fs from 'node:fs/promises';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const argv = process.argv.slice(2);
const outIndex = argv.indexOf('--out');
const out = path.resolve(outIndex === -1 || argv[outIndex + 1] === undefined ? '.archive' : argv[outIndex + 1]);
const token = process.env.GITHUB_TOKEN || process.env.GH_TOKEN;
const repo = process.env.GITHUB_REPOSITORY || execFileSync('gh', ['repo', 'view', '--json', 'nameWithOwner', '-q', '.nameWithOwner'], { encoding: 'utf8' }).trim();
const [owner, name] = repo.split('/');
const headers = { accept: 'application/vnd.github+json', 'user-agent': 'issue-archive' };
if (token) headers.authorization = 'Bearer ' + token;

async function request(url, init) {
    const response = await fetch(url, { ...init, headers: { ...headers, ...(init?.headers || {}) } });
    if (!response.ok) throw new Error(url + ' ' + response.status + ' ' + (await response.text()));
    return response.json();
}

const query = `query ($owner: String!, $repo: String!, $number: Int!) {
  repository(owner: $owner, name: $repo) {
    issue(number: $number) {
      title body state createdAt updatedAt closedAt
      author { login }
      labels(first: 50) { nodes { name } }
      milestone { title }
      userContentEdits(first: 100) { nodes { editedAt diff editor { login } } }
      comments(first: 100) { nodes { id body createdAt author { login } userContentEdits(first: 100) { nodes { editedAt diff editor { login } } } } }
    }
  }
}`;

const numbers = [];
for (let page = 1; ; page++) {
    const batch = await request(`https://api.github.com/repos/${repo}/issues?state=all&per_page=100&page=${page}`);
    for (const issue of batch) if (!issue.pull_request) numbers.push(issue.number);
    if (batch.length < 100) break;
}

await fs.mkdir(path.join(out, 'issues'), { recursive: true });
const index = [];
for (const number of numbers) {
    const result = await request('https://api.github.com/graphql', {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ query, variables: { owner, repo: name, number } }),
    });
    const issue = result.data.repository.issue;
    if (!issue) throw new Error('missing issue #' + number);
    const file = 'issues/' + number + '.json';
    await fs.writeFile(path.join(out, file), JSON.stringify(issue, null, 2) + '\n');
    index.push({ number, title: issue.title, state: issue.state, updatedAt: issue.updatedAt, file });
}
index.sort((a, b) => a.number - b.number);
await fs.writeFile(path.join(out, 'index.json'), JSON.stringify(index, null, 2) + '\n');
