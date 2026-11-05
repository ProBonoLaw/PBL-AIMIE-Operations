import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('public_source', Path(__file__).resolve().parents[2] / 'scripts/check-public-source.py')
scanner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scanner)


class PublicSourceTests(unittest.TestCase):
    def test_deployment_hostname_is_reported_without_its_value(self):
        hostname = 'customer-site.' + 'sharepoint.com'
        result = list(scanner.findings('settings.json', 'https://' + hostname))
        self.assertEqual(result, [(1, 'deployment-hostname')])
        self.assertNotIn(hostname, str(result))

    def test_non_example_identifier_and_configuration_file_are_rejected(self):
        identifier = '-'.join(['12345678', 'abcd', '1234', 'abcd', '123456789abc'])
        self.assertEqual(list(scanner.findings('.env', identifier)),
                         [(1, 'private-or-generated-file'), (1, 'non-example-identifier')])

    def test_examples_and_microsoft_application_ids_are_allowed(self):
        examples = ['https://sharepoint.example.test', '11111111-1111-1111-1111-111111111111']
        for value in examples + list(scanner.PUBLIC_IDS):
            self.assertEqual(list(scanner.findings('fixture.txt', value)), [])


if __name__ == '__main__':
    unittest.main()
