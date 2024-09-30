// References for LINQPad
// Add NuGet packages:
// - Newtonsoft.Json
// - System.Text.Json
// - Microsoft.CSharp

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using System.Linq;

// 1. Define interfaces for Agents and Tools

// Base interface for pipeline steps (agents and tools)
public interface IPipelineStep
{
    Task ExecuteAsync(PipelineContext context);
}

public interface IAgent : IPipelineStep
{
    string Name { get; }
    string Description { get; }
    string SystemPrompt { get; }
}

public interface ITool : IPipelineStep
{
    string Name { get; }
    string Description { get; }
}

// 2. Define the PipelineContext to hold shared data
public class PipelineContext
{
    public Dictionary<string, object> Data { get; } = new Dictionary<string, object>();

    // You can add methods here to manipulate the context if needed
}

// 3. Implement the PipelineBuilder with conditional and sequence steps
public class PipelineBuilder
{
    private readonly List<IPipelineStep> _steps = new List<IPipelineStep>();

    private PipelineBuilder() { }

    public static PipelineBuilder CreatePipeline()
    {
        return new PipelineBuilder();
    }

    public PipelineBuilder AddStep(IPipelineStep step)
    {
        _steps.Add(step);
        return this;
    }

    public PipelineBuilder AddAgent(IAgent agent)
    {
        _steps.Add(agent);
        return this;
    }

    public PipelineBuilder AddTool(ITool tool)
    {
        _steps.Add(tool);
        return this;
    }

    public PipelineBuilder AddConditionalStep(Func<PipelineContext, bool> condition, Action<PipelineBuilder> trueBranch, Action<PipelineBuilder> falseBranch = null)
    {
        var truePipeline = new PipelineBuilder();
        trueBranch(truePipeline);

        PipelineBuilder falsePipeline = null;
        if (falseBranch != null)
        {
            falsePipeline = new PipelineBuilder();
            falseBranch(falsePipeline);
        }

        _steps.Add(new ConditionalStep(condition, truePipeline.Build(), falsePipeline?.Build()));
        return this;
    }

    public PipelineBuilder AddSequence(Action<PipelineBuilder> sequenceBuilder)
    {
        var sequencePipeline = new PipelineBuilder();
        sequenceBuilder(sequencePipeline);
        _steps.Add(new SequenceStep(sequencePipeline.Build()));
        return this;
    }

    public List<IPipelineStep> Build()
    {
        return _steps;
    }

    public async Task<PipelineContext> ExecuteAsync()
    {
        var context = new PipelineContext();

        foreach (var step in _steps)
        {
            await step.ExecuteAsync(context);
        }

        return context;
    }
}

// New step types
public class ConditionalStep : IPipelineStep
{
    private readonly Func<PipelineContext, bool> _condition;
    private readonly List<IPipelineStep> _trueSteps;
    private readonly List<IPipelineStep> _falseSteps;

    public ConditionalStep(Func<PipelineContext, bool> condition, List<IPipelineStep> trueSteps, List<IPipelineStep> falseSteps = null)
    {
        _condition = condition;
        _trueSteps = trueSteps;
        _falseSteps = falseSteps ?? new List<IPipelineStep>();
    }

    public async Task ExecuteAsync(PipelineContext context)
    {
        if (_condition(context))
        {
            foreach (var step in _trueSteps)
            {
                await step.ExecuteAsync(context);
            }
        }
        else if (_falseSteps != null)
        {
            foreach (var step in _falseSteps)
            {
                await step.ExecuteAsync(context);
            }
        }
    }
}

public class SequenceStep : IPipelineStep
{
    private readonly List<IPipelineStep> _steps;

    public SequenceStep(List<IPipelineStep> steps)
    {
        _steps = steps;
    }

    public async Task ExecuteAsync(PipelineContext context)
    {
        foreach (var step in _steps)
        {
            await step.ExecuteAsync(context);
        }
    }
}

// 4. Implement the LLM client interface
public interface ILLMClient
{
    Task<string> GetResponseAsync(string prompt);
}

// 5. Implement the LMStudioClient class
public class LMStudioClient : ILLMClient
{
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public LMStudioClient(string apiKey = "lm-studio", string model = "hugging-quants/Llama-3.2-3B-Instruct-Q8_0-GGUF")
    {
        _apiKey = apiKey;
        _model = model;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public async Task<string> GetResponseAsync(string prompt)
    {
        try
        {
            var messages = new[]
            {
                new { role = "user", content = prompt }
            };

            var requestBody = new
            {
                model = _model,
                messages = messages,
                max_tokens = 1000,
                temperature = 0.7
            };

            var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("http://localhost:1234/v1/chat/completions", content);

            var responseString = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error: {response.StatusCode}");
                Console.WriteLine("Response content:");
                Console.WriteLine(responseString);
                return null;
            }

            dynamic jsonResponse = JsonConvert.DeserializeObject(responseString);
            var messageContent = jsonResponse.choices[0].message.content.ToString();

            return messageContent;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception occurred: {ex.Message}");
            return null;
        }
    }
}

// 6. Implement the Agent class
public class Agent : IAgent
{
    private readonly ILLMClient _llmClient;

    public string Name { get; }
    public string Description { get; }
    public string SystemPrompt { get; }

    public Agent(string name, string description, string systemPrompt, ILLMClient llmClient)
    {
        Name = name;
        Description = description;
        SystemPrompt = systemPrompt;
        _llmClient = llmClient;
    }

    public virtual async Task ExecuteAsync(PipelineContext context)
    {
        Console.WriteLine($"Agent '{Name}' is executing...");

        // Use the LLM client to get a response based on the system prompt
        string response = await _llmClient.GetResponseAsync(SystemPrompt);

        if (string.IsNullOrEmpty(response))
        {
            context.Data[$"{Name}_Result"] = null;
        }
        else
        {
            context.Data[$"{Name}_Result"] = response;
        }
    }
}

// 7. Implement the JsonValidatorTool
public class JsonValidatorTool : ITool
{
    public string Name { get; }
    public string Description { get; }
    private readonly string _inputKey;
    private readonly int _maxRetries;
    private readonly IAgent _agent;

    public JsonValidatorTool(string name, string description, string inputKey, IAgent agent, int maxRetries = 3)
    {
        Name = name;
        Description = description;
        _inputKey = inputKey;
        _agent = agent;
        _maxRetries = maxRetries;
    }

    public async Task ExecuteAsync(PipelineContext context)
    {
        Console.WriteLine($"Tool '{Name}' is validating JSON...");

        int attempts = 0;
        bool isValid = false;
        string input = null;

        while (attempts < _maxRetries && !isValid)
        {
            attempts++;

            // Get the input from the context
            if (context.Data.TryGetValue(_inputKey, out var obj) && obj is string str)
            {
                input = str;
            }
            else
            {
                Console.WriteLine($"Input key '{_inputKey}' not found or not a string.");
                return;
            }

            // Validate the JSON
            isValid = ValidateJson(input);

            if (isValid)
            {
                Console.WriteLine("Validation successful.");
                context.Data[$"{Name}_Result"] = input;
            }
            else
            {
                Console.WriteLine($"Validation failed on attempt {attempts}. Retrying agent '{_agent.Name}'...");

                // Re-execute the agent
                await _agent.ExecuteAsync(context);

                // Update the input with the new result
                if (context.Data.TryGetValue($"{_agent.Name}_Result", out var newObj) && newObj is string newStr)
                {
                    input = newStr;
                    context.Data[_inputKey] = newStr;
                }
                else
                {
                    Console.WriteLine("Agent did not produce a valid result.");
                    input = null;
                }
            }
        }

        if (!isValid)
        {
            throw new Exception($"Validation failed after {_maxRetries} attempts.");
        }
    }

    private bool ValidateJson(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        try
        {
            // Try to parse the input string as JSON
            JsonDocument.Parse(input);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            // Parsing failed; input is not valid JSON
            return false;
        }
    }
}

// 8. Define data classes for deserialization
public class Questionnaire
{
    public List<Question> Questions { get; set; }
}

public class Question
{
    public string QuestionText { get; set; }
    public string QuestionType { get; set; }
    public List<string> Options { get; set; }
}

// 9. Implement additional tools and agents
public class QuestionnaireProcessorTool : ITool
{
    public string Name { get; } = "QuestionnaireProcessorTool";
    public string Description { get; } = "Processes the questionnaire and sets flags in the context.";
    private readonly string _inputKey;

    public QuestionnaireProcessorTool(string inputKey)
    {
        _inputKey = inputKey;
    }

    public Task ExecuteAsync(PipelineContext context)
    {
        Console.WriteLine($"Tool '{Name}' is processing the questionnaire...");

        if (context.Data.TryGetValue(_inputKey, out var obj) && obj is string questionnaireJson)
        {
            try
            {
                var questionnaire = System.Text.Json.JsonSerializer.Deserialize<Questionnaire>(questionnaireJson);
                context.Data["Questionnaire"] = questionnaire; // Store in context

                // Example condition: check if any question is of type "OpenEnded"
                bool hasOpenEndedQuestions = questionnaire.Questions.Any(q => q.QuestionType == "OpenEnded");

                // Set a flag in the context
                context.Data["HasOpenEndedQuestions"] = hasOpenEndedQuestions;

                Console.WriteLine($"Questionnaire processed. HasOpenEndedQuestions: {hasOpenEndedQuestions}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to process questionnaire: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Input key '{_inputKey}' not found or not a string.");
        }

        return Task.CompletedTask;
    }
}

public class OpenEndedQuestionAgent : Agent
{
    public OpenEndedQuestionAgent(string name, string description, string systemPrompt, ILLMClient llmClient)
        : base(name, description, systemPrompt, llmClient)
    {
    }

    public override async Task ExecuteAsync(PipelineContext context)
    {
        Console.WriteLine($"Agent '{Name}' is processing open-ended questions...");

		// Get the questionnaire from the context
		if (context.Data.TryGetValue("Questionnaire", out var obj) && obj is Questionnaire questionnaire)
		{
			// Process open-ended questions
			var openEndedQuestions = questionnaire.Questions.Where(q => q.QuestionType == "OpenEnded").ToList();

			foreach (var question in openEndedQuestions)
			{
				// For each open-ended question, you might want to do something
				Console.WriteLine($"Processing OpenEnded Question: {question.QuestionText}");
				// Simulate processing
			}

			context.Data[$"{Name}_Result"] = "Processed open-ended questions.";
		}
		else
		{
			Console.WriteLine("Questionnaire not found in context.");
		}

		await Task.CompletedTask;
	}
}

public class MultipleChoiceAgent : Agent
{
	public MultipleChoiceAgent(string name, string description, string systemPrompt, ILLMClient llmClient)
		: base(name, description, systemPrompt, llmClient)
	{
	}

	public override async Task ExecuteAsync(PipelineContext context)
	{
		Console.WriteLine($"Agent '{Name}' is processing multiple-choice questions...");

		// Get the questionnaire from the context
		if (context.Data.TryGetValue("Questionnaire", out var obj) && obj is Questionnaire questionnaire)
		{
			// Process multiple-choice questions
			var multipleChoiceQuestions = questionnaire.Questions.Where(q => q.QuestionType == "MultipleChoice").ToList();

			foreach (var question in multipleChoiceQuestions)
			{
				// For each multiple-choice question, you might want to do something
				Console.WriteLine($"Processing MultipleChoice Question: {question.QuestionText}");
				// Simulate processing
			}

			context.Data[$"{Name}_Result"] = "Processed multiple-choice questions.";
		}
		else
		{
			Console.WriteLine("Questionnaire not found in context.");
		}

		await Task.CompletedTask;
	}
}

// 10. Main method to execute the pipeline
public async Task Main()
{
	var llmClient = new LMStudioClient();

	// Define the system prompt for the agent
	string systemPrompt = @"
You are an assistant that generates medical questionnaires.

Please generate a medical questionnaire in JSON format for a patient experiencing headaches.

The JSON should be structured as follows:

{
  ""Questions"": [
    {
      ""QuestionText"": ""..."",
      ""QuestionType"": ""..."" (e.g., ""YesNo"", ""MultipleChoice"", ""OpenEnded""),
      ""Options"": [ ""..."", ""..."" ] // Optional, only for MultipleChoice questions
    },
    // more questions...
  ]
}

Ensure that the JSON is valid and follows the structure above.
";

	// Create the main agent
	var agent = new Agent(
		name: "MedicalQuestionnaireAgent",
		description: "Generates a medical questionnaire in JSON format.",
		systemPrompt: systemPrompt,
		llmClient: llmClient);

	// Create the JSON validator tool
	var jsonValidator = new JsonValidatorTool(
		name: "JsonValidatorTool",
		description: "Validates that the agent's output is valid JSON.",
		inputKey: $"{agent.Name}_Result",
		agent: agent,
		maxRetries: 3);

	// Create the questionnaire processor tool
	var questionnaireProcessor = new QuestionnaireProcessorTool($"{jsonValidator.Name}_Result");

	// Create agents for different question types
	var openEndedQuestionAgent = new OpenEndedQuestionAgent(
		name: "OpenEndedQuestionAgent",
		description: "Processes open-ended questions.",
		systemPrompt: "Process the open-ended questions.",
		llmClient: llmClient);

	var multipleChoiceAgent = new MultipleChoiceAgent(
		name: "MultipleChoiceAgent",
		description: "Processes multiple-choice questions.",
		systemPrompt: "Process the multiple-choice questions.",
		llmClient: llmClient);

	// Build the pipeline with conditional and sequence steps
	var pipeline = PipelineBuilder
		.CreatePipeline()
		.AddAgent(agent)
		.AddTool(jsonValidator)
		.AddTool(questionnaireProcessor)
		.AddConditionalStep(
			condition: context =>
			{
				// Condition based on the context
				return context.Data.TryGetValue("HasOpenEndedQuestions", out var value) && value is bool hasOpenEnded && hasOpenEnded;
			},
			trueBranch: truePipeline =>
			{
				// Steps to execute if the condition is true
				truePipeline.AddAgent(openEndedQuestionAgent);
			},
			falseBranch: falsePipeline =>
			{
				// Steps to execute if the condition is false
				falsePipeline.AddAgent(multipleChoiceAgent);
			}
		);

	// Execute the pipeline
	try
	{
		PipelineContext context = await pipeline.ExecuteAsync();

		// Retrieve the final result from the context
		string finalResult = context.Data[$"{jsonValidator.Name}_Result"] as string;

		if (!string.IsNullOrEmpty(finalResult))
		{
			Console.WriteLine("Final Valid JSON Response:");
			Console.WriteLine(finalResult);

			// Deserialize the JSON into a C# object
			var questionnaire = System.Text.Json.JsonSerializer.Deserialize<Questionnaire>(finalResult);
			context.Data["Questionnaire"] = questionnaire; // Store in context

			Console.WriteLine("Deserialized Questionnaire Object:");
			questionnaire.Dump(); // LINQPad-specific method to display objects
		}
		else
		{
			Console.WriteLine("No valid result was produced.");
		}
	}
	catch (Exception ex)
	{
		Console.WriteLine($"Pipeline execution failed: {ex.Message}");
	}
}
