"""Secret-free regression checks for deployment .env handling."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location('deployment_env', Path(__file__).with_name('deployment-env.py'))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DeploymentEnvTests(unittest.TestCase):
    def values(self):
        return {
            'DOCKERHUB_USERNAME': 'example', 'DOCKERHUB_TOKEN': 'test-token',
            'FieldSense__AdminEmail': 'admin@example.com', 'FieldSense__AdminPassword': 'test-password',
            'FieldSense__JwtKey': 'a' * 48,
            'FieldSense__AcsConnectionString': 'endpoint=https://example.communication.azure.com/;accesskey=test',
            'FieldSense__AcsSenderAddress': 'sender@example.com', 'FieldSense__AcsMock': 'false',
            'FieldSense__CorsOrigin': 'https://sms.example.com',
            'FieldSense__PublicApiBase': 'https://sms.example.com',
        }

    def test_parser_preserves_secret_punctuation_without_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '.env'
            path.write_text('export VALUE="literal # $(command) `command`" # comment\nOTHER=a=b;c # comment\n')
            self.assertEqual(module.load(path), {'VALUE': 'literal # $(command) `command`', 'OTHER': 'a=b;c'})

    def test_duplicate_keys_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '.env'
            path.write_text('VALUE=one\nVALUE=two\n')
            with self.assertRaises(ValueError):
                module.load(path)

    def test_dockerhub_user_alias(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / '.env'
            path.write_text('DOCKERHUB_USER=example\n')
            self.assertEqual(module.load(path)['DOCKERHUB_USERNAME'], 'example')

    def test_validation_detects_missing_and_development_credentials(self):
        values = self.values()
        self.assertEqual(module.validate(values), [])
        del values['DOCKERHUB_TOKEN']
        values['FieldSense__JwtKey'] = 'dev-placeholder'
        values['FieldSense__AcsMock'] = 'true'
        errors = module.validate(values)
        self.assertIn('Missing DOCKERHUB_TOKEN', errors)
        self.assertTrue(any('placeholder' in error for error in errors))
        self.assertTrue(any('AcsMock' in error for error in errors))
        self.assertFalse(any('test-password' in error for error in errors))

    def test_generated_resources_are_scoped_and_use_exact_env_values(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            values = self.values()
            module.render(values, output)
            documents = [json.loads(path.read_text()) for path in output.iterdir()]
            for document in documents:
                for item in document.get('items', [document]):
                    self.assertEqual(item['metadata']['namespace'], 'darvazeh')
            secret = json.loads((output / '20-secret.yaml').read_text())
            self.assertEqual(secret['stringData'], {**{key: values[key] for key in module.SECRET_KEYS},
                **{key: '' for key in module.OPTIONAL_SECRET_KEYS}})
            registry = json.loads((output / '21-registry-secret.yaml').read_text())
            auth = json.loads(registry['stringData']['.dockerconfigjson'])['auths']['https://index.docker.io/v1/']
            self.assertEqual(auth['password'], values['DOCKERHUB_TOKEN'])
            config = (output / '10-configmap.yaml').read_text()
            self.assertNotIn(values['FieldSense__AdminPassword'], config)
            self.assertNotIn(values['DOCKERHUB_TOKEN'], config)
            self.assertIn('https://sms.example.com', config)
            self.assertIn('mobile-config.json', config)

    def test_internal_deployment_does_not_require_public_hostname(self):
        values = self.values()
        del values['FieldSense__PublicApiBase']
        self.assertEqual(module.validate(values), [])

    def test_stripe_credentials_required_only_when_enabled_and_kept_private(self):
        values = self.values()
        values['Billing__StripeEnabled'] = 'true'
        self.assertTrue(any('StripeSecretKey' in error for error in module.validate(values)))
        values['Billing__StripeSecretKey'] = 'sk_test_fixture'
        values['Billing__StripeWebhookSecret'] = 'whsec_fixture'
        self.assertEqual(module.validate(values), [])
        with tempfile.TemporaryDirectory() as directory:
            module.render(values, Path(directory))
            config = (Path(directory) / '10-configmap.yaml').read_text()
            self.assertNotIn('sk_test_fixture', config)
            self.assertNotIn('whsec_fixture', config)
            secret = json.loads((Path(directory) / '20-secret.yaml').read_text())['stringData']
            self.assertEqual(secret['Billing__StripeSecretKey'], 'sk_test_fixture')

    def test_google_is_optional_and_secret_is_never_a_configmap(self):
        values = self.values()
        self.assertEqual(module.validate(values), [])
        values['Google__Enabled'] = 'true'
        self.assertTrue(any('Google__ClientId' in e for e in module.validate(values)))
        values.update(Google__ClientId='fixture.apps.googleusercontent.com', Google__ClientSecret='fixture-server-secret',
            Google__RedirectUri='https://sms.example.com/api/auth/google/callback', Google__PortalOrigin='https://sms.example.com')
        self.assertEqual(module.validate(values), [])
        with tempfile.TemporaryDirectory() as directory:
            module.render(values, Path(directory))
            self.assertNotIn('fixture-server-secret', (Path(directory)/'10-configmap.yaml').read_text())
            secret=json.loads((Path(directory)/'20-secret.yaml').read_text())['stringData']
            self.assertEqual(secret['Google__ClientSecret'], 'fixture-server-secret')
        values['Google__RedirectUri']='https://sms.example.com/another-callback'
        self.assertTrue(any('Google__RedirectUri' in e for e in module.validate(values)))


if __name__ == '__main__':
    unittest.main()
