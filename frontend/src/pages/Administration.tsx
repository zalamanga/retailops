import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { params, write } from '../api';
import {
  Empty,
  ErrorNotice,
  Field,
  Form,
  Icon,
  Loading,
  Modal,
  PageHeading,
  Pagination,
  Status,
  Table,
  text,
  useResource,
  useUser,
} from '../components';
import { date, label, passwordMessage } from '../format';
import { roles, type Activity, type PageResult, type TeamUser } from '../types';

export function ActivityPage() {
  const [page, setPage] = useState(1);
  const query = useResource<PageResult<Activity>>(
    ['activity', page],
    `/activity${params({ page })}`,
  );
  return (
    <>
      <PageHeading
        title="Activity log"
        description="Review who changed workspace records and when the change happened."
      />
      <section className="panel">
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading workspace activity…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Workspace activity log">
                <thead>
                  <tr>
                    <th>When / actor</th>
                    <th>Action</th>
                    <th>Record</th>
                    <th>Description</th>
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((event) => (
                    <tr key={event.id}>
                      <td>
                        {date(event.createdAt, true)}
                        <small>{event.actorName}</small>
                      </td>
                      <td>
                        <strong>{label(event.action)}</strong>
                      </td>
                      <td>
                        {label(event.entityType)}
                        <small className="record-id">{event.entityId}</small>
                      </td>
                      <td className="activity-description">{event.description}</td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty title="No activity recorded">
                Workspace changes and authentication events appear here after they are saved.
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
      <p className="page-note">
        Activity records are read-only in this application. Stock quantities are recorded separately
        in the movement ledger.
      </p>
    </>
  );
}
export function Team() {
  const user = useUser();
  const client = useQueryClient();
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<TeamUser | 'new' | null>(null);
  const query = useResource<PageResult<TeamUser>>(['users', page], `/users${params({ page })}`);
  const member = editing && editing !== 'new' ? editing : null;
  const ownAccount = member?.id === user.id;
  return (
    <>
      <PageHeading
        title="Team & access"
        description="Give each person the access they need to do their part in the purchasing process."
        action={
          <button className="button primary" onClick={() => setEditing('new')}>
            <Icon name="plus" />
            Add team member
          </button>
        }
      />
      <div className="notice information">
        <strong>Approval requires another person.</strong>
        <p>
          Add at least one other Manager or Admin. The person who creates an order cannot approve
          it.
        </p>
      </div>
      <section className="panel">
        <ErrorNotice error={query.error} retry={() => void query.refetch()} />
        {query.isLoading && <Loading text="Loading team members and permissions…" />}
        {query.data && (
          <>
            {query.data.items.length ? (
              <Table label="Team members">
                <thead>
                  <tr>
                    <th>Person / email</th>
                    <th>Role</th>
                    <th>Access</th>
                    <th>
                      <span className="sr-only">Actions</span>
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {query.data.items.map((member) => (
                    <tr key={member.id}>
                      <td>
                        <strong>
                          {member.fullName}
                          {member.id === user.id && ' (you)'}
                        </strong>
                        <small>{member.email}</small>
                      </td>
                      <td>{member.role}</td>
                      <td>
                        <Status value={member.isActive ? 'Active' : 'Deactivated'} />
                      </td>
                      <td className="actions">
                        <button
                          className="button text"
                          onClick={() => setEditing(member)}
                          aria-label={`Edit access for ${member.fullName}`}
                        >
                          Edit access
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </Table>
            ) : (
              <Empty title="No team members found">
                Reload the list to check your workspace members.
              </Empty>
            )}
            <Pagination result={query.data} page={page} onPage={setPage} />
          </>
        )}
      </section>
      <section className="role-reference">
        <h2>Roles at a glance</h2>
        <dl>
          <div>
            <dt>Admin</dt>
            <dd>All operational actions and team access management.</dd>
          </div>
          <div>
            <dt>Manager</dt>
            <dd>Catalog maintenance, order decisions, and warehouse operations.</dd>
          </div>
          <div>
            <dt>Operator</dt>
            <dd>Prepare purchase orders, receive stock, adjust, and transfer.</dd>
          </div>
          <div>
            <dt>Viewer</dt>
            <dd>Read workspace records and export inventory.</dd>
          </div>
          <div>
            <dt>Cashier</dt>
            <dd>Assigned branches, own shift and own receipts. No purchase costs or back-office records.</dd>
          </div>
        </dl>
      </section>
      {editing && (
        <Modal
          title={member ? `Access · ${member.fullName}` : 'Add team member'}
          onClose={() => setEditing(null)}
        >
          <Form
            onCancel={() => setEditing(null)}
            submitLabel={member ? 'Save access' : 'Create team member'}
            submit={async (data) => {
              if (member)
                await write(
                  `/users/${member.id}`,
                  {
                    role: ownAccount ? user.role : text(data, 'role'),
                    isActive: ownAccount || data.get('isActive') === 'on',
                  },
                  'PATCH',
                );
              else {
                const password = String(data.get('password'));
                const message = passwordMessage(password);
                if (message) throw new Error(message);
                await write('/users', {
                  fullName: text(data, 'fullName'),
                  email: text(data, 'email'),
                  password,
                  role: text(data, 'role'),
                });
              }
              await client.invalidateQueries();
              setEditing(null);
            }}
          >
            {!member && (
              <>
                <Field label="Full name">
                  <input
                    name="fullName"
                    required
                    minLength={2}
                    maxLength={100}
                    autoComplete="off"
                    autoFocus
                  />
                </Field>
                <Field label="Email address">
                  <input name="email" type="email" required maxLength={254} autoComplete="off" />
                </Field>
                <Field
                  label="Initial password"
                  hint="12-128 characters, with uppercase, lowercase, and a number. Share the credential through your approved private channel."
                >
                  <input
                    name="password"
                    type="password"
                    required
                    minLength={12}
                    maxLength={128}
                    autoComplete="new-password"
                  />
                </Field>
              </>
            )}
            <Field label="Role">
              <select name="role" defaultValue={member?.role ?? 'Operator'} disabled={ownAccount}>
                {roles.map((role) => (
                  <option key={role} value={role}>
                    {role}
                  </option>
                ))}
              </select>
            </Field>
            {member && (
              <label className="checkbox">
                <input
                  type="checkbox"
                  name="isActive"
                  defaultChecked={member.isActive}
                  disabled={ownAccount}
                />
                Allow this person to sign in
              </label>
            )}
            {ownAccount && (
              <p className="field-note">
                Your own account must remain an active Admin. Ask another Admin to change your
                access if needed.
              </p>
            )}
            {member && !ownAccount && (
              <p className="field-note">
                Deactivation ends this person’s access immediately. Their historical orders and
                activity remain available.
              </p>
            )}
          </Form>
        </Modal>
      )}
    </>
  );
}
