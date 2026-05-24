export interface ScanResult {
  jobName:           string;
  scanType:          number;
  failuresDetected:  number;
  classifications:   number;
  recommendations:   number;
  detail:            string;
}
