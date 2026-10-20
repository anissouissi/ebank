import { useEffect, useState } from 'react'
import type { FormEvent } from 'react'
import keycloak from './auth/keycloak'

type DocumentType = 'identity' | 'address-proof'
type KycStatus = 'Incomplete' | 'PendingReview' | 'Approved' | 'Rejected'

type KycProfile = {
  firstName: string
  lastName: string
  phoneNumber: string
  addressLine1: string
  addressLine2: string
  city: string
  postalCode: string
  country: string
  status: KycStatus
  identityDocument: { mediaType: string; uploadedAt: string } | null
  addressProof: { mediaType: string; uploadedAt: string } | null
  updatedAt: string | null
}

const EMPTY_PROFILE: KycProfile = {
  firstName: '',
  lastName: '',
  phoneNumber: '',
  addressLine1: '',
  addressLine2: '',
  city: '',
  postalCode: '',
  country: '',
  status: 'Incomplete',
  identityDocument: null,
  addressProof: null,
  updatedAt: null,
}

const STATUS_COPY: Record<KycStatus, string> = {
  Incomplete: 'In progress',
  PendingReview: 'Awaiting review',
  Approved: 'Verified',
  Rejected: 'Action required',
}

async function authorizedFetch(url: string, init?: RequestInit) {
  await keycloak.updateToken(30)
  if (!keycloak.authenticated || !keycloak.token) {
    throw new Error('Your session has expired. Sign in again to access your profile.')
  }

  const headers = new Headers(init?.headers)
  headers.set('Authorization', `Bearer ${keycloak.token}`)
  return fetch(url, { ...init, headers })
}

async function readError(response: Response) {
  if (response.status === 401) {
    try {
      const problem = await response.json() as { detail?: string }
      if (problem.detail) return problem.detail
    } catch {
      return 'The API rejected your sign-in token. Check the API terminal for the authentication failure type.'
    }

    return 'The API rejected your sign-in token. Check the API terminal for the authentication failure type.'
  }

  try {
    const problem = await response.json() as {
      detail?: string
      title?: string
      errors?: Record<string, string[]>
    }
    const validationMessage = problem.errors
      ? Object.values(problem.errors).flat().join(' ')
      : undefined
    return validationMessage || problem.detail || problem.title || `Request failed (${response.status}).`
  } catch {
    return `Request failed (${response.status}).`
  }
}

function KycSection() {
  const [profile, setProfile] = useState<KycProfile>(EMPTY_PROFILE)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [message, setMessage] = useState<string | null>(null)

  useEffect(() => {
    let active = true

    authorizedFetch('/api/kyc/')
      .then(async (response) => {
        if (!response.ok) throw new Error(await readError(response))
        return await response.json() as KycProfile
      })
      .then((data) => {
        if (active) setProfile(data)
      })
      .catch((loadError: unknown) => {
        if (active) {
          setError(loadError instanceof Error ? loadError.message : 'Could not load your KYC details.')
        }
      })
      .finally(() => {
        if (active) setLoading(false)
      })

    return () => { active = false }
  }, [])

  const updateField = (field: keyof Pick<
    KycProfile,
    'firstName' | 'lastName' | 'phoneNumber' | 'addressLine1' |
    'addressLine2' | 'city' | 'postalCode' | 'country'
  >, value: string) => {
    setProfile((current) => ({ ...current, [field]: value }))
  }

  const saveProfile = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    setSaving(true)
    setError(null)
    setMessage(null)

    try {
      const response = await authorizedFetch('/api/kyc/profile', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          firstName: profile.firstName,
          lastName: profile.lastName,
          phoneNumber: profile.phoneNumber,
          addressLine1: profile.addressLine1,
          addressLine2: profile.addressLine2,
          city: profile.city,
          postalCode: profile.postalCode,
          country: profile.country,
        }),
      })
      if (!response.ok) throw new Error(await readError(response))
      setProfile(await response.json() as KycProfile)
      setMessage('Your details have been saved.')
    } catch (saveError) {
      setError(saveError instanceof Error ? saveError.message : 'Could not save your details.')
    } finally {
      setSaving(false)
    }
  }

  const acceptDocument = (updatedProfile: KycProfile) => {
    setProfile(updatedProfile)
    setMessage('Document uploaded securely.')
    setError(null)
  }

  return (
    <section className="kyc-card" id="kyc" aria-labelledby="kyc-heading">
      <div className="kyc-heading-row">
        <div>
          <span className="eyebrow">CUSTOMER VERIFICATION</span>
          <h2 id="kyc-heading">Your details &amp; documents</h2>
          <p>Provide your contact details, a photo ID, and a recent bill showing your address.</p>
        </div>
        <span className={`kyc-status status-${profile.status.toLowerCase()}`}>
          <i />{STATUS_COPY[profile.status]}
        </span>
      </div>

      {error && <div className="error-banner" role="alert">{error}</div>}
      {message && <p className="kyc-success" role="status">{message}</p>}

      {loading ? (
        <p className="kyc-loading">Loading your saved details…</p>
      ) : (
        <>
          <form className="kyc-profile-form" onSubmit={saveProfile}>
            <label>
              <span>Legal first name</span>
              <input autoComplete="given-name" maxLength={100} required value={profile.firstName}
                onChange={(event) => updateField('firstName', event.target.value)} />
            </label>
            <label>
              <span>Legal last name</span>
              <input autoComplete="family-name" maxLength={100} required value={profile.lastName}
                onChange={(event) => updateField('lastName', event.target.value)} />
            </label>
            <label>
              <span>Phone number <small>SMS verification isn’t connected yet</small></span>
              <input autoComplete="tel" maxLength={32} minLength={7} type="tel" required
                placeholder="+1 202 555 0123" value={profile.phoneNumber}
                onChange={(event) => updateField('phoneNumber', event.target.value)} />
            </label>
            <label className="address-wide">
              <span>Street address</span>
              <input autoComplete="address-line1" maxLength={200} required value={profile.addressLine1}
                onChange={(event) => updateField('addressLine1', event.target.value)} />
            </label>
            <label className="address-wide">
              <span>Apartment, suite, etc. <small>Optional</small></span>
              <input autoComplete="address-line2" maxLength={200} value={profile.addressLine2}
                onChange={(event) => updateField('addressLine2', event.target.value)} />
            </label>
            <label>
              <span>City</span>
              <input autoComplete="address-level2" maxLength={100} required value={profile.city}
                onChange={(event) => updateField('city', event.target.value)} />
            </label>
            <label>
              <span>Postal code</span>
              <input autoComplete="postal-code" maxLength={32} required value={profile.postalCode}
                onChange={(event) => updateField('postalCode', event.target.value)} />
            </label>
            <label>
              <span>Country code <small>2 letters, e.g. US</small></span>
              <input autoComplete="country" maxLength={2} minLength={2} required
                placeholder="US" value={profile.country}
                onChange={(event) => updateField('country', event.target.value.toUpperCase())} />
            </label>
            <div className="kyc-save-row">
              <button className="button button-primary" disabled={saving}>
                {saving ? 'Saving…' : 'Save details'}
              </button>
            </div>
          </form>

          <div className="kyc-documents">
            <div className="kyc-documents-heading">
              <div>
                <h3>Verification documents</h3>
                <p>PDF, PNG, or JPEG · up to 5 MB each</p>
              </div>
              <span>Files are encrypted before storage</span>
            </div>
            <div className="kyc-document-grid">
              <DocumentUpload
                type="identity"
                title="Government-issued ID"
                description="Upload a clear photo of your identity card or passport."
                current={profile.identityDocument}
                onUploaded={acceptDocument}
              />
              <DocumentUpload
                type="address-proof"
                title="Proof of address"
                description="Upload a recent utility or service bill showing your address."
                current={profile.addressProof}
                onUploaded={acceptDocument}
              />
            </div>
          </div>

          <div className="kyc-review-note">
            <span className="kyc-note-icon">i</span>
            <p>Once your details and both documents are submitted, your application will be marked for review. Uploading documents does not automatically verify your identity or address.</p>
          </div>
        </>
      )}
    </section>
  )
}

function DocumentUpload({ type, title, description, current, onUploaded }: {
  type: DocumentType
  title: string
  description: string
  current: KycProfile['identityDocument']
  onUploaded: (profile: KycProfile) => void
}) {
  const [file, setFile] = useState<File | null>(null)
  const [uploading, setUploading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const upload = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!file) return
    const form = event.currentTarget

    setUploading(true)
    setError(null)

    try {
      const body = new FormData()
      body.append('file', file)
      const response = await authorizedFetch(`/api/kyc/documents/${type}`, {
        method: 'PUT',
        body,
      })
      if (!response.ok) throw new Error(await readError(response))
      onUploaded(await response.json() as KycProfile)
      setFile(null)
      form.reset()
    } catch (uploadError) {
      setError(uploadError instanceof Error ? uploadError.message : 'Could not upload this document.')
    } finally {
      setUploading(false)
    }
  }

  return (
    <form className="kyc-document" onSubmit={upload}>
      <div className="document-icon" aria-hidden="true">▤</div>
      <div className="document-copy">
        <strong>{title}</strong>
        <p>{description}</p>
        {current && (
          <small className="document-uploaded">
            Uploaded {current.uploadedAt.slice(0, 10)}
          </small>
        )}
      </div>
      <label className="file-picker">
        <span>{file ? file.name : 'Choose a file'}</span>
        <input
          type="file"
          accept=".pdf,.png,.jpg,.jpeg,application/pdf,image/png,image/jpeg"
          required={!current}
          onChange={(event) => setFile(event.target.files?.[0] ?? null)}
        />
      </label>
      {error && <p className="document-error" role="alert">{error}</p>}
      <button className="button button-quiet document-upload-button" disabled={!file || uploading}>
        {uploading ? 'Uploading…' : current ? 'Replace document' : 'Upload document'}
      </button>
    </form>
  )
}

export default KycSection
