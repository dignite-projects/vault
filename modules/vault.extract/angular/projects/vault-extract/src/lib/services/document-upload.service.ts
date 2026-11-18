import { Injectable, inject } from '@angular/core';
import { RestService } from '@abp/ng.core';
import { Observable } from 'rxjs';
import type { DocumentDto } from '../proxy/documents/models';

/**
 * Hand-written, proxy-external upload wrapper.
 *
 * Lives OUTSIDE `proxy/` so it survives `abp generate-proxy -t ng` regeneration
 * (the generator overwrites everything under proxy/ — see proxy/README.md).
 *
 * The generated `DocumentService.upload` takes a `FormData` (since the ABP 11 proxies) but
 * leaves its form field names to the caller. This wrapper is the one place that knows them
 * (`File`, `CabinetId`, `DocumentTypeId`), and `buildUploadFormData` is unit-tested; older
 * schematics emitted `IRemoteStreamContent` metadata instead of the bytes.
 */

// Pure helper, exported for unit testing without an Angular injection context (#623):
// builds the multipart body the upload endpoint expects, including the optional
// `DocumentTypeId` declared-type field (a declared type is an operator confirmation —
// see UploadDocumentInput on the backend — so it is only appended when actually set).
export function buildUploadFormData(
  file: File,
  cabinetId?: string,
  documentTypeId?: string,
): FormData {
  const formData = new FormData();
  formData.append('File', file, file.name);
  if (cabinetId) {
    formData.append('CabinetId', cabinetId);
  }
  if (documentTypeId) {
    formData.append('DocumentTypeId', documentTypeId);
  }
  return formData;
}

@Injectable({ providedIn: 'root' })
export class DocumentUploadService {
  private readonly rest = inject(RestService);
  private readonly apiName = 'Default';

  upload = (file: File, cabinetId?: string, documentTypeId?: string): Observable<DocumentDto> => {
    const formData = buildUploadFormData(file, cabinetId, documentTypeId);
    return this.rest.request<FormData, DocumentDto>(
      { method: 'POST', url: '/api/vault-extract/documents/upload', body: formData },
      { apiName: this.apiName },
    );
  };
}
